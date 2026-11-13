using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SpotifyAutoPause
{
    static class Program
    {
        private const int WM_APPCOMMAND = 0x0319;
        private static readonly IntPtr APPCOMMAND_MEDIA_PLAY_PAUSE = (IntPtr)(14 << 16);

        private static bool pausedByScript = false;
        private static bool monitoringEnabled = true;
        private static DateTime lastActionTime = DateTime.MinValue;
        private static string currentColorState = "";

        private static NotifyIcon trayIcon;
        private static ToolStripMenuItem toggleMenuItem;
        private static Timer monitorTimer;
        private static SettingsForm settingsForm;

        // 포터블 구조: 실행 파일 바로 옆에 ignore_list.txt 저장
        private static readonly string ConfigFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ignore_list.txt"
        );

        // 시작프로그램 폴더 내 바로가기 경로 (레지스트리 미사용)
        private static readonly string StartupShortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "SpotifyAutoPause.lnk"
        );

        public static readonly HashSet<string> SystemProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "consent",
            "explorer",
            "shellexperiencehost",
            "searchhost"
        };

        private static readonly HashSet<string> DefaultUserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "discord",
            "kakaotalk"
        };

        public static HashSet<string> UserIgnoreProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static bool IgnoreSystemSounds = true;

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            var settingsMenuItem = new ToolStripMenuItem("예외 프로그램 설정...", null, OnOpenSettings);
            settingsMenuItem.Font = new Font(settingsMenuItem.Font, FontStyle.Bold);

            toggleMenuItem = new ToolStripMenuItem("감시 활성화", null, OnToggleMonitoring)
            {
                Checked = true
            };
            var exitMenuItem = new ToolStripMenuItem("종료", null, OnExit);

            var contextMenu = new ContextMenuStrip();
            contextMenu.Items.Add(settingsMenuItem);
            contextMenu.Items.Add(new ToolStripSeparator());
            contextMenu.Items.Add(toggleMenuItem);
            contextMenu.Items.Add(exitMenuItem);

            trayIcon = new NotifyIcon
            {
                Icon = CreateCircleIcon(Color.FromArgb(29, 185, 84)),
                Text = "Spotify 자동 일시정지 실행 중 (더블클릭하여 설정)",
                ContextMenuStrip = contextMenu,
                Visible = true
            };
            trayIcon.DoubleClick += OnOpenSettings;
            currentColorState = "green";

            monitorTimer = new Timer
            {
                Interval = 400
            };
            monitorTimer.Tick += MonitorTick;
            monitorTimer.Start();

            Application.Run();
        }

        public static bool IsStartupEnabled()
        {
            return File.Exists(StartupShortcutPath);
        }

        public static void SetStartup(bool enable)
        {
            try
            {
                if (enable)
                {
                    Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                    if (shellType != null)
                    {
                        dynamic shell = Activator.CreateInstance(shellType);
                        dynamic shortcut = shell.CreateShortcut(StartupShortcutPath);
                        shortcut.TargetPath = Application.ExecutablePath;
                        shortcut.WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory;
                        shortcut.Description = "Spotify AutoPause";
                        shortcut.Save();
                        Marshal.ReleaseComObject(shortcut);
                        Marshal.ReleaseComObject(shell);
                    }
                }
                else
                {
                    if (File.Exists(StartupShortcutPath))
                        File.Delete(StartupShortcutPath);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("시작프로그램 설정 중 오류가 발생했습니다:\n" + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static bool IsProcessIgnored(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return true;
            if (procName.Equals("spotify", StringComparison.OrdinalIgnoreCase)) return true;
            if (IgnoreSystemSounds && SystemProcesses.Contains(procName)) return true;
            return UserIgnoreProcesses.Contains(procName);
        }

        public static void LoadConfig()
        {
            UserIgnoreProcesses.Clear();
            IgnoreSystemSounds = true;

            if (File.Exists(ConfigFilePath))
            {
                foreach (var line in File.ReadAllLines(ConfigFilePath))
                {
                    var trimmed = line.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#")) continue;

                    if (trimmed.StartsWith("ignore_system="))
                    {
                        IgnoreSystemSounds = trimmed.EndsWith("true");
                        continue;
                    }

                    if (trimmed.EndsWith(".exe"))
                        trimmed = trimmed.Substring(0, trimmed.Length - 4);

                    if (!SystemProcesses.Contains(trimmed) && trimmed != "spotify")
                    {
                        UserIgnoreProcesses.Add(trimmed);
                    }
                }
            }
            else
            {
                foreach (var item in DefaultUserProcesses)
                    UserIgnoreProcesses.Add(item);
                SaveConfig();
            }
        }

        public static void SaveConfig()
        {
            try
            {
                var lines = new List<string>
                {
                    "ignore_system=" + (IgnoreSystemSounds ? "true" : "false")
                };
                lines.AddRange(UserIgnoreProcesses.OrderBy(x => x));
                File.WriteAllLines(ConfigFilePath, lines.ToArray());
            }
            catch { }
        }

        private static void OnOpenSettings(object sender, EventArgs e)
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm();
                settingsForm.Show();
            }
            else
            {
                if (settingsForm.WindowState == FormWindowState.Minimized)
                    settingsForm.WindowState = FormWindowState.Normal;
                settingsForm.Activate();
            }
        }

        private static void MonitorTick(object sender, EventArgs e)
        {
            if (!monitoringEnabled)
            {
                UpdateTrayIcon("gray", Color.Gray);
                return;
            }

            string activeProc = GetActiveProcessName();
            bool spotifyPlaying = false;
            bool activeSound = false;
            bool anyOtherSound = false;

            try
            {
                CheckAudioSessions(activeProc, out spotifyPlaying, out activeSound, out anyOtherSound);
            }
            catch { }

            if ((DateTime.Now - lastActionTime).TotalSeconds > 1.5)
            {
                if (spotifyPlaying && pausedByScript)
                {
                    pausedByScript = false;
                }

                if (spotifyPlaying && activeSound && !pausedByScript)
                {
                    if (SendSpotifyPlayPause())
                    {
                        pausedByScript = true;
                        lastActionTime = DateTime.Now;
                    }
                }
                else if (!anyOtherSound && pausedByScript && !string.IsNullOrEmpty(activeProc))
                {
                    if (SendSpotifyPlayPause())
                    {
                        pausedByScript = false;
                        lastActionTime = DateTime.Now;
                    }
                }
            }

            if (pausedByScript)
                UpdateTrayIcon("orange", Color.Orange);
            else
                UpdateTrayIcon("green", Color.FromArgb(29, 185, 84));
        }

        private static void UpdateTrayIcon(string state, Color color)
        {
            if (currentColorState == state) return;
            var oldIcon = trayIcon.Icon;
            trayIcon.Icon = CreateCircleIcon(color);
            if (oldIcon != null)
            {
                DestroyIcon(oldIcon.Handle);
                oldIcon.Dispose();
            }
            currentColorState = state;
        }

        private static Icon CreateCircleIcon(Color color)
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.FillEllipse(brush, 4, 4, 24, 24);
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private static void OnToggleMonitoring(object sender, EventArgs e)
        {
            monitoringEnabled = !monitoringEnabled;
            toggleMenuItem.Checked = monitoringEnabled;
            if (!monitoringEnabled && pausedByScript)
            {
                SendSpotifyPlayPause();
                pausedByScript = false;
            }
        }

        private static void OnExit(object sender, EventArgs e)
        {
            monitorTimer.Stop();
            trayIcon.Visible = false;
            Application.Exit();
        }

        private static string GetActiveProcessName()
        {
            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return "";
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return "";
                using (var proc = Process.GetProcessById((int)pid))
                {
                    return proc.ProcessName.ToLowerInvariant();
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool SendSpotifyPlayPause()
        {
            var spotifyPids = new HashSet<uint>();
            foreach (var p in Process.GetProcessesByName("Spotify"))
            {
                spotifyPids.Add((uint)p.Id);
                p.Dispose();
            }

            if (spotifyPids.Count == 0) return false;

            bool sent = false;
            EnumWindows((hwnd, lParam) =>
            {
                if (sent) return false;
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (spotifyPids.Contains(pid))
                {
                    if (GetWindowTextLength(hwnd) > 0 || IsWindowVisible(hwnd))
                    {
                        SendMessage(hwnd, WM_APPCOMMAND, IntPtr.Zero, APPCOMMAND_MEDIA_PLAY_PAUSE);
                        sent = true;
                        return false;
                    }
                }
                return true;
            }, IntPtr.Zero);

            return sent;
        }

        private static void CheckAudioSessions(string activeProc, out bool spotifyPlaying, out bool activeSound, out bool anyOtherSound)
        {
            spotifyPlaying = false;
            activeSound = false;
            anyOtherSound = false;

            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice speakers;
            enumerator.GetDefaultAudioEndpoint(0, 1, out speakers);

            Guid iidSessionManager2 = typeof(IAudioSessionManager2).GUID;
            object o;
            speakers.Activate(ref iidSessionManager2, 0, IntPtr.Zero, out o);
            var mgr = (IAudioSessionManager2)o;

            IAudioSessionEnumerator sessionEnumerator;
            mgr.GetSessionEnumerator(out sessionEnumerator);
            int count;
            sessionEnumerator.GetCount(out count);

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl ctl;
                sessionEnumerator.GetSession(i, out ctl);
                var ctl2 = ctl as IAudioSessionControl2;
                var meter = ctl as IAudioMeterInformation;

                if (ctl2 != null && meter != null)
                {
                    uint pid;
                    ctl2.GetProcessId(out pid);
                    if (pid > 0)
                    {
                        float peak;
                        meter.GetPeakValue(out peak);
                        if (peak > 0.001f)
                        {
                            string procName = "";
                            try
                            {
                                using (var p = Process.GetProcessById((int)pid))
                                {
                                    procName = p.ProcessName.ToLowerInvariant();
                                }
                            }
                            catch { }

                            if (!string.IsNullOrEmpty(procName))
                            {
                                if (procName == "spotify")
                                {
                                    spotifyPlaying = true;
                                }
                                else if (!IsProcessIgnored(procName))
                                {
                                    anyOtherSound = true;
                                    if (procName == activeProc)
                                    {
                                        activeSound = true;
                                    }
                                }
                            }
                        }
                    }
                }
                if (ctl != null) Marshal.ReleaseComObject(ctl);
            }

            Marshal.ReleaseComObject(sessionEnumerator);
            Marshal.ReleaseComObject(mgr);
            Marshal.ReleaseComObject(speakers);
            Marshal.ReleaseComObject(enumerator);
        }

        #region Win32 & CoreAudio COM Interop
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int NotImpl1();
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            int NotImpl1();
            int NotImpl2();
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int SessionCount);
            [PreserveSig]
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            int NotImpl1();
            int NotImpl2();
            int NotImpl3();
            int NotImpl4();
            int NotImpl5();
            int NotImpl6();
            int NotImpl7();
            int NotImpl8();
            int NotImpl9();
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            int NotImpl1();
            int NotImpl2();
            int NotImpl3();
            int NotImpl4();
            int NotImpl5();
            int NotImpl6();
            int NotImpl7();
            int NotImpl8();
            int NotImpl9();
            int NotImpl10();
            int NotImpl11();
            [PreserveSig]
            int GetProcessId(out uint pRetVal);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            [PreserveSig]
            int GetPeakValue(out float pfPeak);
        }
        #endregion
    }

    public class ProcItem
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public override string ToString() { return Name; }
    }

    public class SettingsForm : Form
    {
        private ListView listViewIgnore;
        private ImageList iconList;
        private ComboBox comboRunningProcs;
        private CheckBox chkIgnoreSystem;
        private CheckBox chkRunAtStartup;
        private Button btnRefresh;
        private Button btnAdd;
        private Button btnRemove;

        private readonly Dictionary<string, string> cachedExePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public SettingsForm()
        {
            Text = "Spotify AutoPause - 예외 프로그램 설정";
            Size = new Size(450, 470);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 9.5f);

            iconList = new ImageList
            {
                ImageSize = new Size(18, 18),
                ColorDepth = ColorDepth.Depth32Bit
            };
            iconList.Images.Add("default", SystemIcons.Application.ToBitmap());

            var lblDesc = new Label
            {
                Text = "아래 목록에 등록된 앱은 소리가 나도 Spotify가 멈추지 않습니다.",
                Location = new Point(15, 15),
                Size = new Size(405, 22)
            };

            listViewIgnore = new ListView
            {
                Location = new Point(15, 42),
                Size = new Size(405, 205),
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.None,
                SmallImageList = iconList,
                MultiSelect = false
            };
            listViewIgnore.Columns.Add("프로그램", 380);

            var lblInput = new Label
            {
                Text = "실행 중인 앱 선택 또는 직접 입력 (예: chrome):",
                Location = new Point(15, 258),
                Size = new Size(405, 20)
            };

            comboRunningProcs = new ComboBox
            {
                Location = new Point(15, 282),
                Size = new Size(245, 28),
                DropDownStyle = ComboBoxStyle.DropDown,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22
            };
            comboRunningProcs.DrawItem += ComboRunningProcs_DrawItem;

            btnRefresh = new Button
            {
                Text = "새로고침",
                Location = new Point(266, 281),
                Size = new Size(75, 28)
            };
            btnRefresh.Click += (s, e) =>
            {
                RefreshRunningProcesses();
                RefreshIgnoreListView();
            };

            btnAdd = new Button
            {
                Text = "추가",
                Location = new Point(346, 281),
                Size = new Size(74, 28)
            };
            btnAdd.Click += OnAddClick;

            btnRemove = new Button
            {
                Text = "선택 항목 삭제",
                Location = new Point(15, 320),
                Size = new Size(405, 32)
            };
            btnRemove.Click += OnRemoveClick;

            chkIgnoreSystem = new CheckBox
            {
                Text = "Windows 시스템 알림음 및 권한 팝업(UAC) 소리 무시 (권장)",
                Location = new Point(16, 365),
                Size = new Size(405, 24),
                Checked = Program.IgnoreSystemSounds
            };
            chkIgnoreSystem.CheckedChanged += (s, e) =>
            {
                Program.IgnoreSystemSounds = chkIgnoreSystem.Checked;
                Program.SaveConfig();
            };

            chkRunAtStartup = new CheckBox
            {
                Text = "Windows 시작 시 자동 실행",
                Location = new Point(16, 393),
                Size = new Size(405, 24),
                Checked = Program.IsStartupEnabled()
            };
            chkRunAtStartup.CheckedChanged += (s, e) =>
            {
                Program.SetStartup(chkRunAtStartup.Checked);
                chkRunAtStartup.Checked = Program.IsStartupEnabled();
            };

            Controls.Add(lblDesc);
            Controls.Add(listViewIgnore);
            Controls.Add(lblInput);
            Controls.Add(comboRunningProcs);
            Controls.Add(btnRefresh);
            Controls.Add(btnAdd);
            Controls.Add(btnRemove);
            Controls.Add(chkIgnoreSystem);
            Controls.Add(chkRunAtStartup);

            RefreshRunningProcesses();
            RefreshIgnoreListView();
        }

        private string EnsureProcessIcon(string procName)
        {
            if (iconList.Images.ContainsKey(procName))
                return procName;

            string exePath = null;
            if (!cachedExePaths.TryGetValue(procName, out exePath))
            {
                try
                {
                    var procs = Process.GetProcessesByName(procName);
                    foreach (var p in procs)
                    {
                        try
                        {
                            if (p.MainModule != null && File.Exists(p.MainModule.FileName))
                            {
                                exePath = p.MainModule.FileName;
                                cachedExePaths[procName] = exePath;
                                break;
                            }
                        }
                        catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                try
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (ico != null)
                        {
                            iconList.Images.Add(procName, ico.ToBitmap());
                            return procName;
                        }
                    }
                }
                catch { }
            }

            return "default";
        }

        private void RefreshIgnoreListView()
        {
            listViewIgnore.BeginUpdate();
            listViewIgnore.Items.Clear();

            foreach (var proc in Program.UserIgnoreProcesses.OrderBy(x => x))
            {
                string iconKey = EnsureProcessIcon(proc);
                var item = new ListViewItem(proc, iconKey);
                listViewIgnore.Items.Add(item);
            }

            listViewIgnore.EndUpdate();
        }

        private void RefreshRunningProcesses()
        {
            string currentText = comboRunningProcs.Text;
            comboRunningProcs.Items.Clear();

            var procMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string name = p.ProcessName.ToLowerInvariant();
                    try
                    {
                        if (p.MainModule != null && File.Exists(p.MainModule.FileName))
                            cachedExePaths[name] = p.MainModule.FileName;
                    }
                    catch { }

                    if (!string.IsNullOrWhiteSpace(p.MainWindowTitle))
                    {
                        if (!procMap.ContainsKey(name))
                            procMap[name] = p.MainWindowTitle;
                    }
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            foreach (var kv in procMap.OrderBy(x => x.Key))
            {
                EnsureProcessIcon(kv.Key);
                comboRunningProcs.Items.Add(new ProcItem { Name = kv.Key, Title = kv.Value });
            }

            comboRunningProcs.Text = currentText;
        }

        private void ComboRunningProcs_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index >= 0 && e.Index < comboRunningProcs.Items.Count)
            {
                var item = comboRunningProcs.Items[e.Index] as ProcItem;
                if (item != null)
                {
                    string iconKey = iconList.Images.ContainsKey(item.Name) ? item.Name : "default";
                    Image img = iconList.Images[iconKey];

                    int imgY = e.Bounds.Top + (e.Bounds.Height - img.Height) / 2;
                    e.Graphics.DrawImage(img, e.Bounds.Left + 4, imgY);

                    string displayText = string.Format("{0}  ({1})", item.Name, item.Title);
                    using (var brush = new SolidBrush(e.ForeColor))
                    {
                        var textRect = new Rectangle(
                            e.Bounds.Left + img.Width + 10,
                            e.Bounds.Top + 2,
                            e.Bounds.Width - img.Width - 12,
                            e.Bounds.Height - 2
                        );
                        e.Graphics.DrawString(displayText, e.Font, brush, textRect, StringFormat.GenericDefault);
                    }
                }
            }
            e.DrawFocusRectangle();
        }

        private void OnAddClick(object sender, EventArgs e)
        {
            string input = comboRunningProcs.Text.Trim().ToLowerInvariant();
            if (input.EndsWith(".exe"))
                input = input.Substring(0, input.Length - 4);

            if (string.IsNullOrEmpty(input)) return;

            if (input == "spotify" || Program.SystemProcesses.Contains(input))
            {
                MessageBox.Show("해당 항목은 시스템 기본 보호 목록에 이미 포함되어 있습니다.", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                comboRunningProcs.Text = "";
                return;
            }

            if (!Program.UserIgnoreProcesses.Contains(input))
            {
                Program.UserIgnoreProcesses.Add(input);
                Program.SaveConfig();
                RefreshIgnoreListView();
            }
            comboRunningProcs.Text = "";
        }

        private void OnRemoveClick(object sender, EventArgs e)
        {
            if (listViewIgnore.SelectedItems.Count == 0) return;
            string selected = listViewIgnore.SelectedItems[0].Text;

            Program.UserIgnoreProcesses.Remove(selected);
            Program.SaveConfig();
            RefreshIgnoreListView();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && iconList != null)
            {
                iconList.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}