using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SpotifyAutoPause
{
    static class Program
    {
        private const int WM_APPCOMMAND = 0x0319;
        private static readonly IntPtr APPCOMMAND_MEDIA_PLAY_PAUSE = (IntPtr)(14 << 16);
        private static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;
        private static readonly uint WM_SHOW_SETTINGS = RegisterWindowMessage("WM_SPOTIFY_AUTOPAUSE_SHOW_SETTINGS");

        private static Mutex appMutex;
        private static bool pausedByScript = false;
        private static bool monitoringEnabled = true;
        private static DateTime lastActionTime = DateTime.MinValue;
        private static DateTime silenceStartTime = DateTime.MinValue;
        private static string currentColorState = "";

        private static NotifyIcon trayIcon;
        private static ToolStripMenuItem toggleMenuItem;
        private static System.Windows.Forms.Timer monitorTimer;
        private static SettingsForm settingsForm;

        private static readonly string ConfigFilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "ignore_list.txt"
        );

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

        // 3가지 프로세스 관리 목록
        public static HashSet<string> UserIgnoreProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 소리 무시 (예외)
        public static HashSet<string> ForceFocusProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // 맨 앞(포커스)일 때 소리 무관 강제 정지
        public static HashSet<string> ForceBackgroundProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // 백그라운드 포함 실행 중일 때 소리 무관 강제 정지

        public static bool IgnoreSystemSounds = true;
        public static double ResumeDelaySeconds = 3.0;
        public static double VolumeThresholdPercent = 0.1;

        [STAThread]
        static void Main()
        {
            bool createdNew;
            appMutex = new Mutex(true, "Local\\SpotifyAutoPause_SingleInstance_Mutex", out createdNew);

            if (!createdNew)
            {
                PostMessage(HWND_BROADCAST, WM_SHOW_SETTINGS, IntPtr.Zero, IntPtr.Zero);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            LoadConfig();

            var msgWindow = new MessageReceiverWindow();

            var settingsMenuItem = new ToolStripMenuItem("프로그램 설정...", null, OnOpenSettings);
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

            monitorTimer = new System.Windows.Forms.Timer
            {
                Interval = 400
            };
            monitorTimer.Tick += MonitorTick;
            monitorTimer.Start();

            Application.Run();

            GC.KeepAlive(msgWindow);
            GC.KeepAlive(appMutex);
        }

        private class MessageReceiverWindow : NativeWindow
        {
            public MessageReceiverWindow()
            {
                CreateHandle(new CreateParams());
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SHOW_SETTINGS)
                {
                    if (settingsForm == null || settingsForm.IsDisposed)
                    {
                        OnOpenSettings(null, EventArgs.Empty);
                    }
                    return;
                }
                base.WndProc(ref m);
            }
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

        private static string NormalizeProcName(string raw)
        {
            string trimmed = raw.Trim().ToLowerInvariant();
            if (trimmed.EndsWith(".exe"))
                trimmed = trimmed.Substring(0, trimmed.Length - 4);
            return trimmed;
        }

        public static void LoadConfig()
        {
            UserIgnoreProcesses.Clear();
            ForceFocusProcesses.Clear();
            ForceBackgroundProcesses.Clear();
            IgnoreSystemSounds = true;
            ResumeDelaySeconds = 3.0;
            VolumeThresholdPercent = 0.1;

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

                    if (trimmed.StartsWith("resume_delay="))
                    {
                        double parsed;
                        if (double.TryParse(trimmed.Substring("resume_delay=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            if (parsed < 0.0) parsed = 0.0;
                            ResumeDelaySeconds = parsed;
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("volume_threshold="))
                    {
                        double parsed;
                        if (double.TryParse(trimmed.Substring("volume_threshold=".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                        {
                            if (parsed < 0.0) parsed = 0.0;
                            if (parsed > 100.0) parsed = 100.0;
                            VolumeThresholdPercent = parsed;
                        }
                        continue;
                    }

                    if (trimmed.StartsWith("focus:"))
                    {
                        string name = NormalizeProcName(trimmed.Substring("focus:".Length));
                        if (!string.IsNullOrEmpty(name) && name != "spotify")
                            ForceFocusProcesses.Add(name);
                        continue;
                    }

                    if (trimmed.StartsWith("bg:"))
                    {
                        string name = NormalizeProcName(trimmed.Substring("bg:".Length));
                        if (!string.IsNullOrEmpty(name) && name != "spotify")
                            ForceBackgroundProcesses.Add(name);
                        continue;
                    }

                    string ignoreName = NormalizeProcName(trimmed);
                    if (!string.IsNullOrEmpty(ignoreName) && !SystemProcesses.Contains(ignoreName) && ignoreName != "spotify")
                    {
                        UserIgnoreProcesses.Add(ignoreName);
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
                    "ignore_system=" + (IgnoreSystemSounds ? "true" : "false"),
                    "resume_delay=" + ResumeDelaySeconds.ToString("0.0", CultureInfo.InvariantCulture),
                    "volume_threshold=" + VolumeThresholdPercent.ToString("0.##", CultureInfo.InvariantCulture)
                };
                lines.AddRange(UserIgnoreProcesses.OrderBy(x => x));
                lines.AddRange(ForceFocusProcesses.OrderBy(x => x).Select(x => "focus:" + x));
                lines.AddRange(ForceBackgroundProcesses.OrderBy(x => x).Select(x => "bg:" + x));
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

            bool spotifyPlaying = false;
            bool shouldPause = false;

            try
            {
                // 1. 오디오 세션 검사 (예외 목록 외 프로그램의 소리 출력 여부)
                CheckAudioSessions(out spotifyPlaying, out shouldPause);

                // 2. 맨 앞(포커스) 앱 강제 정지 목록 검사 (소리 무관)
                if (!shouldPause && ForceFocusProcesses.Count > 0)
                {
                    string activeProc = GetActiveProcessName();
                    if (!string.IsNullOrEmpty(activeProc) && ForceFocusProcesses.Contains(activeProc))
                    {
                        shouldPause = true;
                    }
                }

                // 3. 백그라운드 포함 실행 중 앱 강제 정지 목록 검사 (소리 무관)
                if (!shouldPause && ForceBackgroundProcesses.Count > 0)
                {
                    if (IsAnyTargetProcessRunning(ForceBackgroundProcesses))
                    {
                        shouldPause = true;
                    }
                }
            }
            catch { }

            if ((DateTime.Now - lastActionTime).TotalSeconds > 1.5)
            {
                if (spotifyPlaying && pausedByScript)
                {
                    pausedByScript = false;
                    silenceStartTime = DateTime.MinValue;
                }

                // 일시정지 조건 충족 시 즉시 정지
                if (spotifyPlaying && shouldPause && !pausedByScript)
                {
                    if (SendSpotifyPlayPause())
                    {
                        pausedByScript = true;
                        lastActionTime = DateTime.Now;
                        silenceStartTime = DateTime.MinValue;
                    }
                }
                // 스크립트가 멈춰둔 상태 -> 정지 조건이 모두 해제된 후 유예 시간 경과 시 재개
                else if (pausedByScript)
                {
                    if (shouldPause)
                    {
                        silenceStartTime = DateTime.MinValue;
                    }
                    else
                    {
                        if (silenceStartTime == DateTime.MinValue)
                        {
                            silenceStartTime = DateTime.Now;
                        }
                        else if ((DateTime.Now - silenceStartTime).TotalSeconds >= ResumeDelaySeconds)
                        {
                            if (SendSpotifyPlayPause())
                            {
                                pausedByScript = false;
                                lastActionTime = DateTime.Now;
                                silenceStartTime = DateTime.MinValue;
                            }
                        }
                    }
                }
            }

            if (pausedByScript)
                UpdateTrayIcon("orange", Color.Orange);
            else
                UpdateTrayIcon("green", Color.FromArgb(29, 185, 84));
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

        private static bool IsAnyTargetProcessRunning(HashSet<string> targetSet)
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (targetSet.Contains(p.ProcessName.ToLowerInvariant()))
                        return true;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
            return false;
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
                silenceStartTime = DateTime.MinValue;
            }
        }

        private static void OnExit(object sender, EventArgs e)
        {
            monitorTimer.Stop();
            trayIcon.Visible = false;
            Application.Exit();
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

        private static void CheckAudioSessions(out bool spotifyPlaying, out bool anyOtherSound)
        {
            spotifyPlaying = false;
            anyOtherSound = false;

            float threshold = (float)(VolumeThresholdPercent / 100.0);

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
                                if (peak > 0.001f)
                                    spotifyPlaying = true;
                            }
                            else if (!IsProcessIgnored(procName))
                            {
                                if (peak > threshold)
                                    anyOtherSound = true;
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

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
        private TabControl tabControl;
        private ListView[] listViews = new ListView[3];
        private Label lblTabDesc;

        private ImageList iconList;
        private ToolTip toolTip;
        private ComboBox comboRunningProcs;
        private NumericUpDown numResumeDelay;
        private NumericUpDown numVolumeThreshold;
        private CheckBox chkIgnoreSystem;
        private CheckBox chkRunAtStartup;
        private Button btnRefresh;
        private Button btnAdd;
        private Button btnRemove;

        private readonly Dictionary<string, string> cachedExePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] TabDescriptions = new string[]
        {
            "소리가 나도 Spotify를 멈추지 않고 무시합니다.",
            "해당 앱 창을 보고 있을 때(활성 창) 소리 무관 정지합니다.",
            "백그라운드 포함 실행 중이면 소리 무관 정지합니다."
        };

        public SettingsForm()
        {
            Text = "Spotify AutoPause - 설정";
            Size = new Size(460, 585);
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

            toolTip = new ToolTip
            {
                AutoPopDelay = 10000,
                InitialDelay = 400,
                ReshowDelay = 200,
                ShowAlways = true
            };

            // 상단 3개 모드 탭 컨트롤
            tabControl = new TabControl
            {
                Location = new Point(15, 12),
                Size = new Size(415, 225)
            };

            string[] tabTitles = new string[]
            {
                "예외 앱 (소리 무시)",
                "강제 정지 (맨 앞 화면)",
                "강제 정지 (실행 중)"
            };

            for (int i = 0; i < 3; i++)
            {
                var page = new TabPage(tabTitles[i]);
                var lv = new ListView
                {
                    Dock = DockStyle.Fill,
                    View = View.Details,
                    FullRowSelect = true,
                    HeaderStyle = ColumnHeaderStyle.None,
                    SmallImageList = iconList,
                    MultiSelect = false
                };
                lv.Columns.Add("프로그램", 385);
                page.Controls.Add(lv);
                tabControl.TabPages.Add(page);
                listViews[i] = lv;
            }

            lblTabDesc = new Label
            {
                Text = TabDescriptions[0],
                Location = new Point(16, 243),
                Size = new Size(415, 22),
                ForeColor = Color.DimGray
            };

            tabControl.SelectedIndexChanged += (s, e) =>
            {
                lblTabDesc.Text = TabDescriptions[tabControl.SelectedIndex];
            };

            var lblInput = new Label
            {
                Text = "현재 선택된 탭에 추가할 앱 선택 또는 직접 입력 (예: chrome):",
                Location = new Point(15, 274),
                Size = new Size(415, 20)
            };

            comboRunningProcs = new ComboBox
            {
                Location = new Point(15, 298),
                Size = new Size(255, 28),
                DropDownStyle = ComboBoxStyle.DropDown,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 22
            };
            comboRunningProcs.DrawItem += ComboRunningProcs_DrawItem;

            btnRefresh = new Button
            {
                Text = "새로고침",
                Location = new Point(276, 297),
                Size = new Size(75, 28)
            };
            btnRefresh.Click += (s, e) =>
            {
                RefreshRunningProcesses();
                RefreshAllListViews();
            };

            btnAdd = new Button
            {
                Text = "추가",
                Location = new Point(356, 297),
                Size = new Size(74, 28)
            };
            btnAdd.Click += OnAddClick;

            btnRemove = new Button
            {
                Text = "선택 항목 삭제",
                Location = new Point(15, 336),
                Size = new Size(415, 32)
            };
            btnRemove.Click += OnRemoveClick;

            // 1. 무음 유예 시간 설정
            var lblDelay = new Label
            {
                Text = "음악 재생 유예시간:",
                Location = new Point(16, 384),
                Size = new Size(320, 22)
            };

            numResumeDelay = new NumericUpDown
            {
                Location = new Point(340, 381),
                Size = new Size(70, 26),
                DecimalPlaces = 1,
                Increment = 0.1M,
                Minimum = 0.0M,
                Maximum = decimal.MaxValue,
                Value = (decimal)Program.ResumeDelaySeconds,
                TextAlign = HorizontalAlignment.Right
            };
            numResumeDelay.ValueChanged += (s, e) =>
            {
                Program.ResumeDelaySeconds = (double)numResumeDelay.Value;
                Program.SaveConfig();
            };

            var lblSec = new Label
            {
                Text = "초",
                Location = new Point(413, 384),
                Size = new Size(22, 22)
            };

            // 2. 볼륨 감지 임계값 설정
            var lblThreshold = new Label
            {
                Text = "소리 감지 최소치:",
                Location = new Point(16, 418),
                Size = new Size(320, 22)
            };

            numVolumeThreshold = new NumericUpDown
            {
                Location = new Point(340, 415),
                Size = new Size(70, 26),
                DecimalPlaces = 2,
                Increment = 0.05M,
                Minimum = 0.00M,
                Maximum = 100.00M,
                Value = (decimal)Program.VolumeThresholdPercent,
                TextAlign = HorizontalAlignment.Right
            };
            numVolumeThreshold.ValueChanged += (s, e) =>
            {
                Program.VolumeThresholdPercent = (double)numVolumeThreshold.Value;
                Program.SaveConfig();
            };

            var lblPercent = new Label
            {
                Text = "%",
                Location = new Point(413, 418),
                Size = new Size(22, 22)
            };

            // 3. 체크박스 옵션
            chkIgnoreSystem = new CheckBox
            {
                Text = "Windows 시스템 알림음 및 권한 팝업(UAC) 소리 무시 (권장)",
                Location = new Point(16, 452),
                Size = new Size(415, 24),
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
                Location = new Point(16, 480),
                Size = new Size(415, 24),
                Checked = Program.IsStartupEnabled()
            };
            chkRunAtStartup.CheckedChanged += (s, e) =>
            {
                Program.SetStartup(chkRunAtStartup.Checked);
                chkRunAtStartup.Checked = Program.IsStartupEnabled();
            };

            // 툴팁 설명 등록
            toolTip.SetToolTip(tabControl,
                "• 예외 앱 (소리 무시): 소리가 나도 Spotify를 멈추지 않을 앱 (예: Discord, 카카오톡)\n" +
                "• 강제 정지 (맨 앞 화면): 해당 앱 창을 보고 있을 때만 소리 무관 Spotify 정지\n" +
                "• 강제 정지 (실행 중): 백그라운드에 켜져만 있어도 소리 무관 Spotify 정지");

            toolTip.SetToolTip(comboRunningProcs, "현재 창이 열려 있는 앱을 선택하거나, 작업 관리자 기준 프로세스 이름(.exe 제외)을 직접 입력하세요.");

            string delayHelp = "외부 소리나 강제 정지 조건이 끝난 뒤 Spotify를 다시 재생하기까지 기다리는 시간입니다.\n BGM 등의 공백에서 음악 재생을 방지합니다.";
            toolTip.SetToolTip(lblDelay, delayHelp);
            toolTip.SetToolTip(numResumeDelay, delayHelp);

            string thresholdHelp = "감지할 최소 소리 크기(%)입니다.\n• 소리가 작은 게임이 인식이 안 되면 값을 낮추세요 (예: 0.01%)\n• 미세한 효과음이나 노이즈 때문에 자꾸 멈추면 값을 높이세요 (예: 1.0%)";
            toolTip.SetToolTip(lblThreshold, thresholdHelp);
            toolTip.SetToolTip(numVolumeThreshold, thresholdHelp);

            toolTip.SetToolTip(chkIgnoreSystem, "Windows 알림음, 볼륨 조절 효과음, 관리자 권한(UAC) 팝업 소리 때문에 음악이 끊기는 것을 막습니다.");

            Controls.Add(tabControl);
            Controls.Add(lblTabDesc);
            Controls.Add(lblInput);
            Controls.Add(comboRunningProcs);
            Controls.Add(btnRefresh);
            Controls.Add(btnAdd);
            Controls.Add(btnRemove);
            Controls.Add(lblDelay);
            Controls.Add(numResumeDelay);
            Controls.Add(lblSec);
            Controls.Add(lblThreshold);
            Controls.Add(numVolumeThreshold);
            Controls.Add(lblPercent);
            Controls.Add(chkIgnoreSystem);
            Controls.Add(chkRunAtStartup);

            RefreshRunningProcesses();
            RefreshAllListViews();
        }

        private HashSet<string> GetTargetSet(int tabIndex)
        {
            if (tabIndex == 1) return Program.ForceFocusProcesses;
            if (tabIndex == 2) return Program.ForceBackgroundProcesses;
            return Program.UserIgnoreProcesses;
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

        private void RefreshAllListViews()
        {
            for (int i = 0; i < 3; i++)
            {
                var lv = listViews[i];
                var set = GetTargetSet(i);

                lv.BeginUpdate();
                lv.Items.Clear();

                foreach (var proc in set.OrderBy(x => x))
                {
                    string iconKey = EnsureProcessIcon(proc);
                    lv.Items.Add(new ListViewItem(proc, iconKey));
                }

                lv.EndUpdate();
            }
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
                MessageBox.Show("해당 항목은 시스템 기본 보호 목록에 포함되어 있어 추가할 수 없습니다.", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                comboRunningProcs.Text = "";
                return;
            }

            int idx = tabControl.SelectedIndex;
            var targetSet = GetTargetSet(idx);

            // 다른 탭에 중복 등록되어 있다면 충돌 방지를 위해 기존 탭에서 자동 이동
            Program.UserIgnoreProcesses.Remove(input);
            Program.ForceFocusProcesses.Remove(input);
            Program.ForceBackgroundProcesses.Remove(input);

            targetSet.Add(input);
            Program.SaveConfig();
            RefreshAllListViews();
            comboRunningProcs.Text = "";
        }

        private void OnRemoveClick(object sender, EventArgs e)
        {
            int idx = tabControl.SelectedIndex;
            var lv = listViews[idx];
            if (lv.SelectedItems.Count == 0) return;

            string selected = lv.SelectedItems[0].Text;
            GetTargetSet(idx).Remove(selected);
            Program.SaveConfig();
            RefreshAllListViews();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (iconList != null) iconList.Dispose();
                if (toolTip != null) toolTip.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
