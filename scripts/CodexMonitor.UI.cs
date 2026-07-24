using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexMonitor
{
    internal static class NativeMethods
    {
        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; internal POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] internal struct SIZE { public int Width, Height; internal SIZE(int width, int height) { Width = width; Height = height; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFOHEADER
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BITMAPINFO { public BITMAPINFOHEADER Header; public uint Colors; }

        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] internal static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT destination, ref SIZE size, IntPtr hdcSrc, ref POINT source, int colorKey, ref BLENDFUNCTION blend, uint flags);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr hDc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool DeleteDC(IntPtr hDc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr SelectObject(IntPtr hDc, IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateDIBSection(IntPtr hDc, ref BITMAPINFO bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        internal static readonly IntPtr HwndTopmost = new IntPtr(-1);
        internal static readonly IntPtr HwndTop = IntPtr.Zero;
        internal const uint GaRoot = 2;
        internal const uint SwpNoActivate = 0x0010;
        internal const uint SwpShowWindow = 0x0040;
        internal const uint SwpNoSize = 0x0001;
        internal const uint SwpNoMove = 0x0002;
        internal const uint SwpNoZOrder = 0x0004;
        internal const uint SwpFrameChanged = 0x0020;
        internal const int GwlExStyle = -20;
        internal const int WsExLayered = 0x00080000;
        internal const byte AcSrcOver = 0;
        internal const byte AcSrcAlpha = 1;
        internal const uint UlwAlpha = 0x00000002;
        internal const uint DibRgbColors = 0;
        internal const int DwmwaWindowCornerPreference = 33;
        internal const int DwmwcpDoNotRound = 1;
        internal const int DwmwcpRound = 2;
        internal const int DwmwaBorderColor = 34;
        internal const int DwmColorNone = -2;
        internal static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

        internal static void InitializeDpiAwareness()
        {
            try { SetProcessDpiAwarenessContext(DpiAwarenessPerMonitorV2); }
            catch { try { SetProcessDPIAware(); } catch { } }
            try { SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2); }
            catch { }
        }

        internal static float SystemScale()
        {
            try { return Math.Max(1f, GetDpiForSystem() / 96f); }
            catch { return 1f; }
        }

        internal static IntPtr FindCodexWindow()
        {
            IntPtr selected = IntPtr.Zero;
            long selectedArea = 0;
            EnumWindows(delegate(IntPtr handle, IntPtr parameter)
            {
                if (!IsWindowVisible(handle)) return true;
                uint pid;
                GetWindowThreadProcessId(handle, out pid);
                try
                {
                    Process process = Process.GetProcessById((int)pid);
                    string name = process.ProcessName;
                    if (!name.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("codex", StringComparison.OrdinalIgnoreCase)) return true;
                    RECT rect;
                    if (!GetWindowRect(handle, out rect)) return true;
                    long width = Math.Max(0, rect.Right - rect.Left);
                    long height = Math.Max(0, rect.Bottom - rect.Top);
                    long area = width * height;
                    if (width >= 500 && height >= 400 && area > selectedArea)
                    { selected = handle; selectedArea = area; }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return selected;
        }

        internal static bool IsDesktopShellWindow(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(handle, GaRoot);
            IntPtr shell = GetShellWindow();
            if (shell != IntPtr.Zero && root == GetAncestor(shell, GaRoot)) return true;
            StringBuilder className = new StringBuilder(64);
            if (GetClassName(root, className, className.Capacity) <= 0) return false;
            string value = className.ToString();
            return value.Equals("Progman", StringComparison.OrdinalIgnoreCase)
                || value.Equals("WorkerW", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class WidgetPreferences
    {
        internal string Language = "zh-CN";
        internal bool AlwaysOnTop = true;
        internal bool HasCustomPosition;
        internal int X;
        internal int Y;
    }

    internal static class PreferencesStore
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static WidgetPreferences Read(string path)
        {
            WidgetPreferences result = new WidgetPreferences();
            try
            {
                if (!File.Exists(path)) return result;
                Dictionary<string, object> values = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                object language;
                if (values.TryGetValue("language", out language) && Convert.ToString(language) == "en") result.Language = "en";
                object top;
                bool parsed;
                if (values.TryGetValue("alwaysOnTop", out top) && Boolean.TryParse(Convert.ToString(top), out parsed)) result.AlwaysOnTop = parsed;
                object custom;
                if (values.TryGetValue("hasCustomPosition", out custom) && Boolean.TryParse(Convert.ToString(custom), out parsed)) result.HasCustomPosition = parsed;
                object x;
                int coordinate;
                if (values.TryGetValue("x", out x) && Int32.TryParse(Convert.ToString(x), out coordinate)) result.X = coordinate;
                object y;
                if (values.TryGetValue("y", out y) && Int32.TryParse(Convert.ToString(y), out coordinate)) result.Y = coordinate;
            }
            catch { }
            return result;
        }

        internal static void Write(string path, WidgetPreferences preferences)
        {
            try
            {
                string folder = Path.GetDirectoryName(path);
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                Dictionary<string, object> values = new Dictionary<string, object>();
                values["language"] = preferences.Language;
                values["alwaysOnTop"] = preferences.AlwaysOnTop;
                values["hasCustomPosition"] = preferences.HasCustomPosition;
                values["x"] = preferences.X;
                values["y"] = preferences.Y;
                string temporary = path + "." + Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(values), new UTF8Encoding(false));
                File.Copy(temporary, path, true);
                File.Delete(temporary);
            }
            catch { }
        }
    }

    internal static class UiSelfTest
    {
        internal static int AppendSelfTests(List<string> failures)
        {
            string root = Path.Combine(Path.GetTempPath(), "CodexMonitor.Ui." + Guid.NewGuid().ToString("N"));
            try
            {
                string path = Path.Combine(root, "preferences.json");
                WidgetPreferences expected = new WidgetPreferences {
                    Language = "en",
                    AlwaysOnTop = false,
                    HasCustomPosition = true,
                    X = 321,
                    Y = 654
                };
                PreferencesStore.Write(path, expected);
                WidgetPreferences actual = PreferencesStore.Read(path);
                if (actual.Language != "en" || actual.AlwaysOnTop || !actual.HasCustomPosition)
                    failures.Add("preference flags round trip");
                if (actual.X != 321 || actual.Y != 654)
                    failures.Add("drag position round trip");
                Rectangle work = new Rectangle(0, 0, 1920, 1080);
                Point cornerAnchor = new Point(1872, 1032);
                Rectangle expanded = QuotaMonitorForm.ComputeAnchoredBounds(
                    work, cornerAnchor, new Size(48, 48), new Size(252, 132));
                Rectangle collapsed = QuotaMonitorForm.ComputeAnchoredBounds(
                    work, cornerAnchor, new Size(48, 48), new Size(48, 48));
                if (expanded != new Rectangle(1668, 948, 252, 132))
                    failures.Add("corner expansion stays on-screen");
                if (collapsed.Location != cornerAnchor || collapsed.Size != new Size(48, 48))
                    failures.Add("collapse returns to orb anchor");
                return 4;
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch { }
            }
        }
    }

    internal sealed class StatePalette
    {
        internal Color Cool, Glow, Warm, Signal, ProgressStart, ProgressEnd;
    }

    internal sealed class GlyphButton : Button
    {
        protected override bool ShowFocusCues { get { return false; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!Focused) return;
            using (Pen focus = new Pen(Color.FromArgb(57, 122, 224), 2f))
                e.Graphics.DrawLine(focus, 7, Height - 4, Math.Max(8, Width - 7), Height - 4);
        }
    }

    internal sealed class QuotaMonitorForm : Form
    {
        private static readonly Size OrbLogicalSize = new Size(48, 48);
        private static readonly Size CardLogicalSize = new Size(252, 132);
        private const int ScreenMargin = 18;

        private readonly string sessionsRoot;
        private readonly string historyCachePath;
        private readonly string preferencesPath;
        private readonly bool forceVisible;
        private readonly Timer monitorTimer;
        private readonly Timer animationTimer;
        private readonly Button languageButton;
        private readonly Button topButton;
        private readonly ToolTip toolTip;
        private FileSystemWatcher sessionWatcher;
        private Task<UsageSnapshot> refreshTask;
        private UsageSnapshot usage = new UsageSnapshot { Status = "loading", Message = "Refreshing quota data." };
        private UsageSnapshot lastSuccess;
        private WidgetPreferences preferences;
        private IntPtr codexWindow = IntPtr.Zero;
        private DateTime lastPointerInside = DateTime.MinValue;
        private DateTime lastFetchStarted = DateTime.MinValue;
        private DateTime nextRefresh = DateTime.MinValue;
        private DateTime interactionGraceUntil = DateTime.MinValue;
        private int failures;
        private long sessionDirtyTicks;
        private long handledDirtyTicks;
        private bool wasEligible;
        private bool expandedTarget;
        private bool animationActive;
        private bool nativeCardCornersAvailable;
        private bool layeredOrbActive;
        private int appliedCornerPreference = -1;
        private Size animationFrom;
        private Size animationTo;
        private DateTime animationStart;
        private int animationDuration;
        private bool userClosing;
        private bool dragging;
        private bool dragCandidate;
        private bool dragMoved;
        private Point dragStartScreen;
        private Point dragStartAnchor;
        private float uiScale;
        private TokenDetailsForm detailsForm;

        private readonly Font headerFont = PixelFont("Segoe UI", 11.3f, FontStyle.Bold);
        private readonly Font statusFont = PixelFont("Segoe UI", 9.6f, FontStyle.Bold);
        private readonly Font labelFont = PixelFont("Segoe UI", 10.9f, FontStyle.Regular);
        private readonly Font percentFont = PixelFont("Consolas", 24f, FontStyle.Regular);
        private readonly Font resetFont = PixelFont("Segoe UI", 9.6f, FontStyle.Regular);
        private readonly Font orbFont = PixelFont("Consolas", 14f, FontStyle.Bold);
        private readonly Font orbPercentFont = PixelFont("Segoe UI", 7.7f, FontStyle.Bold);

        internal ApplicationContext Context;

        internal QuotaMonitorForm(string sessionsRootValue, string statePathValue, int autoCloseSeconds, bool forceVisibleValue)
        {
            sessionsRoot = sessionsRootValue;
            string stateFolder = Path.GetDirectoryName(statePathValue);
            historyCachePath = Path.Combine(stateFolder, "token-history-cache.json");
            preferencesPath = Path.Combine(stateFolder, "preferences.json");
            preferences = PreferencesStore.Read(preferencesPath);
            forceVisible = forceVisibleValue;
            uiScale = NativeMethods.SystemScale();
            Text = "Codex Quota Orb";
            ClientSize = ScaleSize(OrbLogicalSize);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = preferences.AlwaysOnTop;
            BackColor = Color.FromArgb(234, 242, 248);
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);

            languageButton = CreateControlButton();
            topButton = CreateControlButton();
            languageButton.Click += delegate { ToggleLanguage(); };
            topButton.Click += delegate { ToggleAlwaysOnTop(); };
            Controls.Add(languageButton);
            Controls.Add(topButton);
            toolTip = new ToolTip { InitialDelay = 250, ReshowDelay = 100, AutoPopDelay = 3500 };
            UpdateControlText();
            UpdateDataToolTip();
            LayoutControls();

            monitorTimer = new Timer { Interval = 250 };
            monitorTimer.Tick += MonitorTick;
            animationTimer = new Timer { Interval = 15 };
            animationTimer.Tick += AnimationTick;

            if (autoCloseSeconds > 0)
            {
                Timer closeTimer = new Timer { Interval = autoCloseSeconds * 1000 };
                closeTimer.Tick += delegate { closeTimer.Stop(); closeTimer.Dispose(); userClosing = true; Close(); };
                closeTimer.Start();
            }
            SetupSessionWatcher();
        }

        private Button CreateControlButton()
        {
            Button button = new GlyphButton();
            button.Size = ScaleSize(new Size(28, 28));
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = Color.Transparent;
            button.FlatAppearance.MouseDownBackColor = Color.Transparent;
            button.BackColor = Color.Transparent;
            button.UseVisualStyleBackColor = false;
            button.ForeColor = Color.FromArgb(31, 38, 49);
            button.Font = PixelFont("Segoe UI", 9.6f * uiScale, FontStyle.Bold);
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Padding = new Padding(0);
            button.TabStop = true;
            return button;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams value = base.CreateParams;
                value.ExStyle |= NativeMethods.WsExLayered;
                value.ExStyle |= 0x00000080;
                return value;
            }
        }

        internal void StartMonitor()
        {
            IntPtr create = Handle;
            try
            {
                int corner = NativeMethods.DwmwcpDoNotRound;
                nativeCardCornersAvailable = NativeMethods.DwmSetWindowAttribute(
                    Handle, NativeMethods.DwmwaWindowCornerPreference, ref corner, Marshal.SizeOf(typeof(int))) == 0;
                appliedCornerPreference = corner;
                int borderColor = NativeMethods.DwmColorNone;
                NativeMethods.DwmSetWindowAttribute(Handle, NativeMethods.DwmwaBorderColor, ref borderColor, Marshal.SizeOf(typeof(int)));
            }
            catch { }
            ApplyWindowBounds(ScaleSize(OrbLogicalSize));
            ApplyRegion();
            monitorTimer.Start();
            MonitorTick(this, EventArgs.Empty);
        }

        private void SetupSessionWatcher()
        {
            try
            {
                if (!Directory.Exists(sessionsRoot)) return;
                sessionWatcher = new FileSystemWatcher(sessionsRoot, "*.jsonl");
                sessionWatcher.IncludeSubdirectories = true;
                sessionWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                FileSystemEventHandler changed = delegate { System.Threading.Interlocked.Exchange(ref sessionDirtyTicks, DateTime.UtcNow.Ticks); };
                RenamedEventHandler renamed = delegate { System.Threading.Interlocked.Exchange(ref sessionDirtyTicks, DateTime.UtcNow.Ticks); };
                sessionWatcher.Changed += changed;
                sessionWatcher.Created += changed;
                sessionWatcher.Renamed += renamed;
                sessionWatcher.EnableRaisingEvents = true;
            }
            catch { }
        }

        private void MonitorTick(object sender, EventArgs args)
        {
            FinishRefreshIfReady();
            IntPtr discovered = NativeMethods.FindCodexWindow();
            if (discovered != IntPtr.Zero) codexWindow = discovered;
            bool eligible = ShouldShowForCodex();
            if (eligible)
            {
                if (!dragging) PositionForCurrentSize();
                if (!Visible) Show();
                ApplyZOrder();
                if (!wasEligible) StartRefresh(true);
                HandleHover();
                HandleRefreshSchedule();
                RefreshVisual();
            }
            else
            {
                if (Visible) Hide();
                if (expandedTarget) SetExpanded(false, true);
            }
            wasEligible = eligible;
        }

        private bool ShouldShowForCodex()
        {
            if (forceVisible || preferences.AlwaysOnTop) return true;
            if (DateTime.UtcNow < interactionGraceUntil) return true;
            if (codexWindow == IntPtr.Zero || !NativeMethods.IsWindowVisible(codexWindow) || NativeMethods.IsIconic(codexWindow)) return false;
            IntPtr foregroundRoot = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRoot);
            IntPtr detailsRoot = detailsForm != null && !detailsForm.IsDisposed && detailsForm.IsHandleCreated
                ? NativeMethods.GetAncestor(detailsForm.Handle, NativeMethods.GaRoot)
                : IntPtr.Zero;
            return foregroundRoot == codexWindow
                || foregroundRoot == Handle
                || (detailsRoot != IntPtr.Zero && foregroundRoot == detailsRoot)
                || NativeMethods.IsDesktopShellWindow(foregroundRoot);
        }

        private void HandleHover()
        {
            Point pointer = PointToClient(Cursor.Position);
            bool orb = IsOrbSize;
            double radius = Math.Min(ClientSize.Width, ClientSize.Height) / 2.0;
            double dx = pointer.X - ClientSize.Width / 2.0;
            double dy = pointer.Y - ClientSize.Height / 2.0;
            bool inside = dragging || (orb ? dx * dx + dy * dy <= radius * radius : ClientRectangle.Contains(pointer));
            DateTime now = DateTime.UtcNow;
            if (inside)
            {
                lastPointerInside = now;
            }
            else if (expandedTarget && now - lastPointerInside > TimeSpan.FromMilliseconds(400))
            {
                SetExpanded(false, false);
                if (!preferences.AlwaysOnTop
                    && NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GaRoot) == Handle
                    && codexWindow != IntPtr.Zero && !NativeMethods.IsIconic(codexWindow))
                    NativeMethods.SetForegroundWindow(codexWindow);
            }
        }

        private void HandleRefreshSchedule()
        {
            DateTime now = DateTime.UtcNow;
            long dirty = System.Threading.Interlocked.Read(ref sessionDirtyTicks);
            if (dirty > handledDirtyTicks && now.Ticks - dirty >= TimeSpan.FromMilliseconds(750).Ticks)
            {
                handledDirtyTicks = dirty;
                StartRefresh(true);
                return;
            }
            if (now >= nextRefresh) StartRefresh(false);
        }

        private void StartRefresh(bool force)
        {
            DateTime now = DateTime.UtcNow;
            if (refreshTask != null || now - lastFetchStarted < TimeSpan.FromSeconds(2)) return;
            if (!force && now < nextRefresh) return;
            lastFetchStarted = now;
            refreshTask = Task.Factory.StartNew<UsageSnapshot>(delegate { return QuotaServiceReader.ReadLatest(); });
        }

        private void FinishRefreshIfReady()
        {
            if (refreshTask == null || !refreshTask.IsCompleted) return;
            UsageSnapshot next;
            try { next = refreshTask.Result; }
            catch { next = new UsageSnapshot { Status = "unavailable", Message = "Quota refresh failed.", SampleUtc = DateTime.UtcNow }; }
            refreshTask = null;

            if (next.Available)
            {
                usage = next;
                lastSuccess = next.Clone();
                failures = 0;
                nextRefresh = DateTime.UtcNow.Add(GetHealthyInterval(next));
            }
            else
            {
                failures++;
                if (lastSuccess != null && DateTime.UtcNow - lastSuccess.SampleUtc <= TimeSpan.FromMinutes(30))
                {
                    usage = lastSuccess.Clone();
                    usage.Status = "stale";
                    usage.Message = next.Message;
                }
                else usage = next;
                double seconds = Math.Min(300, 15 * Math.Pow(2, Math.Min(4, failures - 1)));
                nextRefresh = DateTime.UtcNow.AddSeconds(seconds);
            }
            UpdateDataToolTip();
            RefreshVisual();
        }

        private static TimeSpan GetHealthyInterval(UsageSnapshot value)
        {
            long nextReset = 0;
            foreach (long candidate in new[] { value.SecondaryReset })
            {
                if (candidate <= 0) continue;
                if (nextReset <= 0 || candidate < nextReset) nextReset = candidate;
            }
            if (nextReset > 0)
            {
                DateTime reset = EpochToLocal(nextReset).ToUniversalTime();
                TimeSpan distance = reset - DateTime.UtcNow;
                if (distance > TimeSpan.FromMinutes(-5) && distance <= TimeSpan.FromMinutes(15)) return TimeSpan.FromSeconds(10);
            }
            return TimeSpan.FromSeconds(30);
        }

        private void SetExpanded(bool value, bool immediate)
        {
            if (expandedTarget == value && !immediate) return;
            expandedTarget = value;
            Size target = ScaleSize(value ? CardLogicalSize : OrbLogicalSize);
            if (immediate || !SystemInformation.UIEffectsEnabled)
            {
                animationTimer.Stop(); animationActive = false; ApplyWindowBounds(target); ApplyRegion();
            }
            else
            {
                animationFrom = ClientSize;
                animationTo = target;
                animationStart = DateTime.UtcNow;
                animationDuration = value ? 150 : 100;
                animationActive = true;
                animationTimer.Start();
            }
            UpdateControlVisibility();
        }

        private void AnimationTick(object sender, EventArgs args)
        {
            if (!animationActive) { animationTimer.Stop(); return; }
            double raw = (DateTime.UtcNow - animationStart).TotalMilliseconds / animationDuration;
            double t = Math.Max(0, Math.Min(1, raw));
            double eased = 1 - Math.Pow(1 - t, 3);
            int width = (int)Math.Round(animationFrom.Width + (animationTo.Width - animationFrom.Width) * eased);
            int height = (int)Math.Round(animationFrom.Height + (animationTo.Height - animationFrom.Height) * eased);
            ApplyWindowBounds(new Size(width, height));
            ApplyRegion();
            UpdateControlVisibility();
            RefreshVisual();
            if (t >= 1) { animationActive = false; animationTimer.Stop(); }
        }

        private void ApplyWindowBounds(Size size)
        {
            Screen screen;
            if (preferences.HasCustomPosition)
                screen = Screen.FromPoint(new Point(preferences.X, preferences.Y));
            else
                screen = codexWindow == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(codexWindow);
            Rectangle work = screen.WorkingArea;
            int margin = Scale(ScreenMargin);
            Size orbSize = ScaleSize(OrbLogicalSize);
            int anchorX = preferences.HasCustomPosition ? preferences.X : work.Right - orbSize.Width - margin;
            int anchorY = preferences.HasCustomPosition ? preferences.Y : work.Bottom - orbSize.Height - margin;
            Rectangle targetBounds = ComputeAnchoredBounds(
                work, new Point(anchorX, anchorY), orbSize, size);
            Bounds = targetBounds;
            if (preferences.HasCustomPosition)
            {
                preferences.X = Math.Max(work.Left, Math.Min(work.Right - orbSize.Width, anchorX));
                preferences.Y = Math.Max(work.Top, Math.Min(work.Bottom - orbSize.Height, anchorY));
            }
            LayoutControls();
        }

        internal static Rectangle ComputeAnchoredBounds(Rectangle work, Point anchor, Size orbSize, Size targetSize)
        {
            int anchorX = Math.Max(work.Left, Math.Min(work.Right - orbSize.Width, anchor.X));
            int anchorY = Math.Max(work.Top, Math.Min(work.Bottom - orbSize.Height, anchor.Y));
            int x = Math.Max(work.Left, Math.Min(work.Right - targetSize.Width, anchorX));
            int y = Math.Max(work.Top, Math.Min(work.Bottom - targetSize.Height, anchorY));
            return new Rectangle(x, y, targetSize.Width, targetSize.Height);
        }

        private Point ResolveOrbAnchor()
        {
            Screen screen;
            if (preferences.HasCustomPosition)
                screen = Screen.FromPoint(new Point(preferences.X, preferences.Y));
            else
                screen = codexWindow == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(codexWindow);
            Rectangle work = screen.WorkingArea;
            Size orbSize = ScaleSize(OrbLogicalSize);
            int margin = Scale(ScreenMargin);
            int x = preferences.HasCustomPosition ? preferences.X : work.Right - orbSize.Width - margin;
            int y = preferences.HasCustomPosition ? preferences.Y : work.Bottom - orbSize.Height - margin;
            return new Point(
                Math.Max(work.Left, Math.Min(work.Right - orbSize.Width, x)),
                Math.Max(work.Top, Math.Min(work.Bottom - orbSize.Height, y)));
        }

        private void PositionForCurrentSize()
        {
            ApplyWindowBounds(ClientSize);
        }

        private void ApplyZOrder()
        {
            IntPtr order = preferences.AlwaysOnTop ? NativeMethods.HwndTopmost : NativeMethods.HwndTop;
            NativeMethods.SetWindowPos(Handle, order, Left, Top, Width, Height, NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        }

        private void ApplyRegion()
        {
            bool orb = IsOrbSize;
            if (orb)
            {
                Region previousOrb = Region;
                Region = null;
                if (previousOrb != null) previousOrb.Dispose();
                ApplyNativeCornerPreference(NativeMethods.DwmwcpDoNotRound);
                SetLayeredOrbMode(true);
                RenderLayeredOrb();
                return;
            }

            SetLayeredOrbMode(false);
            if (!orb && nativeCardCornersAvailable)
            {
                Region previousNative = Region;
                Region = null;
                if (previousNative != null) previousNative.Dispose();
                ApplyNativeCornerPreference(NativeMethods.DwmwcpRound);
                return;
            }

            ApplyNativeCornerPreference(NativeMethods.DwmwcpDoNotRound);
            GraphicsPath path = new GraphicsPath();
            using (GraphicsPath rounded = RoundedRect(new Rectangle(0, 0, Math.Max(1, ClientSize.Width - 1), Math.Max(1, ClientSize.Height - 1)), Scale(18)))
                path.AddPath(rounded, false);
            Region previous = Region;
            Region = new Region(path);
            path.Dispose();
            if (previous != null) previous.Dispose();
        }

        private void SetLayeredOrbMode(bool enabled)
        {
            if (!IsHandleCreated || layeredOrbActive == enabled) return;
            int style = NativeMethods.GetWindowLong(Handle, NativeMethods.GwlExStyle);
            int next = enabled ? style | NativeMethods.WsExLayered : style & ~NativeMethods.WsExLayered;
            if (next != style) NativeMethods.SetWindowLong(Handle, NativeMethods.GwlExStyle, next);
            layeredOrbActive = enabled;
            NativeMethods.SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize |
                NativeMethods.SwpNoZOrder | NativeMethods.SwpFrameChanged);
            if (!enabled) Invalidate(true);
        }

        private void RefreshVisual()
        {
            if (layeredOrbActive && IsOrbSize) RenderLayeredOrb();
            else Invalidate();
        }

        private void RenderLayeredOrb()
        {
            if (!IsHandleCreated || ClientSize.Width < 1 || ClientSize.Height < 1) return;
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memoryDc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                screenDc = NativeMethods.GetDC(IntPtr.Zero);
                memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                NativeMethods.BITMAPINFO info = new NativeMethods.BITMAPINFO();
                info.Header.Size = (uint)Marshal.SizeOf(typeof(NativeMethods.BITMAPINFOHEADER));
                info.Header.Width = ClientSize.Width;
                info.Header.Height = -ClientSize.Height;
                info.Header.Planes = 1;
                info.Header.BitCount = 32;
                IntPtr bits;
                dib = NativeMethods.CreateDIBSection(screenDc, ref info, NativeMethods.DibRgbColors, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) return;
                previous = NativeMethods.SelectObject(memoryDc, dib);
                using (Bitmap bitmap = new Bitmap(ClientSize.Width, ClientSize.Height, ClientSize.Width * 4, PixelFormat.Format32bppPArgb, bits))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = CompositingMode.SourceCopy;
                    graphics.Clear(Color.Transparent);
                    graphics.CompositingMode = CompositingMode.SourceOver;
                    graphics.CompositingQuality = CompositingQuality.HighQuality;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    graphics.ScaleTransform(uiScale, uiScale);
                    DrawOrb(graphics);
                }
                NativeMethods.POINT destination = new NativeMethods.POINT(Left, Top);
                NativeMethods.POINT source = new NativeMethods.POINT(0, 0);
                NativeMethods.SIZE size = new NativeMethods.SIZE(ClientSize.Width, ClientSize.Height);
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION {
                    BlendOp = NativeMethods.AcSrcOver, BlendFlags = 0,
                    SourceConstantAlpha = 255, AlphaFormat = NativeMethods.AcSrcAlpha
                };
                NativeMethods.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, NativeMethods.UlwAlpha);
            }
            finally
            {
                if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) NativeMethods.SelectObject(memoryDc, previous);
                if (dib != IntPtr.Zero) NativeMethods.DeleteObject(dib);
                if (memoryDc != IntPtr.Zero) NativeMethods.DeleteDC(memoryDc);
                if (screenDc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void ApplyNativeCornerPreference(int preference)
        {
            if (!nativeCardCornersAvailable || appliedCornerPreference == preference || !IsHandleCreated) return;
            try
            {
                int value = preference;
                if (NativeMethods.DwmSetWindowAttribute(
                    Handle, NativeMethods.DwmwaWindowCornerPreference, ref value, Marshal.SizeOf(typeof(int))) == 0)
                    appliedCornerPreference = preference;
            }
            catch { }
        }

        private void LayoutControls()
        {
            languageButton.Bounds = ScaleRectangle(new Rectangle(184, 5, 28, 28));
            topButton.Bounds = ScaleRectangle(new Rectangle(216, 5, 28, 28));
        }

        private void UpdateControlVisibility()
        {
            bool visible = expandedTarget && LogicalClientWidth >= 210;
            languageButton.Visible = visible;
            topButton.Visible = visible;
        }

        private void ToggleLanguage()
        {
            preferences.Language = preferences.Language == "en" ? "zh-CN" : "en";
            PreferencesStore.Write(preferencesPath, preferences);
            UpdateControlText();
            UpdateDataToolTip();
            RefreshVisual();
        }

        private void ToggleAlwaysOnTop()
        {
            preferences.AlwaysOnTop = !preferences.AlwaysOnTop;
            TopMost = preferences.AlwaysOnTop;
            PreferencesStore.Write(preferencesPath, preferences);
            UpdateControlText();
            ApplyZOrder();
            RefreshVisual();
        }

        private void UpdateControlText()
        {
            bool chinese = preferences.Language != "en";
            languageButton.Text = chinese ? "EN" : "中";
            languageButton.AccessibleName = chinese ? "切换为英文" : "Switch to Chinese";
            topButton.Text = preferences.AlwaysOnTop ? "↑" : "·";
            topButton.AccessibleName = chinese
                ? (preferences.AlwaysOnTop ? "取消始终置顶" : "始终置顶")
                : (preferences.AlwaysOnTop ? "Disable always on top" : "Always on top");
            toolTip.SetToolTip(languageButton, languageButton.AccessibleName);
            toolTip.SetToolTip(topButton, topButton.AccessibleName);
        }

        private void UpdateDataToolTip()
        {
            bool chinese = preferences.Language != "en";
            string text;
            if (usage.Status == "loading") text = chinese ? "正在同步 Codex 配额" : "Syncing Codex quota";
            else if (usage.Status == "stale") text = chinese ? "接口暂不可用，正在显示最近一次成功数据" : "Service unavailable; showing the latest successful sample";
            else if (usage.Status == "signed_out") text = chinese ? "请先登录 Codex Desktop" : "Sign in to Codex Desktop first";
            else if (!usage.Available) text = chinese ? "配额服务暂不可用，将自动重试" : "Quota service unavailable; retrying automatically";
            else text = (chinese ? "已验证 " : "Verified ") + usage.SampleUtc.ToLocalTime().ToString("HH:mm:ss");
            toolTip.SetToolTip(this, text);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (layeredOrbActive && IsOrbSize) return;
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            e.Graphics.ScaleTransform(uiScale, uiScale);
            if (LogicalClientWidth < 150) DrawOrb(e.Graphics); else DrawCard(e.Graphics);
        }

        private void DrawOrb(Graphics g)
        {
            double remaining = usage.SecondaryAvailable ? 100 - usage.SecondaryUsed : -1;
            StatePalette palette = PaletteFor(remaining);
            int width = LogicalClientWidth;
            int height = LogicalClientHeight;
            Rectangle bounds = new Rectangle(1, 1, width - 3, height - 3);
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, palette.Glow, palette.Cool, 135f)) g.FillEllipse(fill, bounds);
            using (Pen edge = new Pen(Color.FromArgb(78, palette.Signal), 1f)) g.DrawEllipse(edge, bounds);
            if (remaining >= 0)
            {
                using (Pen progress = new Pen(palette.Signal, 3f))
                { progress.StartCap = LineCap.Round; progress.EndCap = LineCap.Round; g.DrawArc(progress, new Rectangle(5, 5, width - 10, height - 10), -90, (float)(360 * remaining / 100)); }
                string value = Math.Round(remaining).ToString("0");
                SizeF size = g.MeasureString(value, orbFont);
                using (SolidBrush ink = new SolidBrush(Color.FromArgb(24, 29, 37)))
                using (SolidBrush muted = new SolidBrush(Color.FromArgb(75, 84, 96)))
                {
                    g.DrawString(value, orbFont, ink, (bounds.Width - size.Width) / 2f - 1, 14);
                    g.DrawString("%", orbPercentFont, muted, bounds.Width / 2f + size.Width / 2f - 2, 25);
                }
            }
            else
            {
                string mark = usage.Status == "signed_out" ? "!" : usage.Status == "loading" ? "..." : "—";
                SizeF size = g.MeasureString(mark, orbFont);
                using (SolidBrush signal = new SolidBrush(palette.Signal))
                    g.DrawString(mark, orbFont, signal, (bounds.Width - size.Width) / 2f, 14);
            }
        }

        private void DrawCard(Graphics g)
        {
            double weekly = usage.SecondaryAvailable ? 100 - usage.SecondaryUsed : -1;
            double overall = weekly;
            StatePalette palette = PaletteFor(overall);
            g.Clear(palette.Cool);
            Rectangle bounds = new Rectangle(0, 0, LogicalClientWidth - 1, LogicalClientHeight - 1);
            using (LinearGradientBrush fill = new LinearGradientBrush(bounds, palette.Cool, palette.Glow, 150f)) g.FillRectangle(fill, bounds);

            bool chinese = preferences.Language != "en";
            string plan = String.IsNullOrWhiteSpace(usage.Plan) ? "—" : usage.Plan;
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 27, 34)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(83, 94, 108)))
            using (SolidBrush signal = new SolidBrush(palette.Signal))
            {
                const float headerCenterY = 19f;
                string title = "CODEX  ·  " + plan;
                float titleY = headerCenterY - g.MeasureString(title, headerFont).Height / 2f;
                g.DrawString(title, headerFont, ink, 14, titleY);
                string status = StatusLabel(overall, usage.Status, chinese);
                float statusY = headerCenterY - g.MeasureString(status, statusFont).Height / 2f;
                g.FillEllipse(signal, 126, headerCenterY - 3f, 6, 6);
                g.DrawString(status, statusFont, secondary, 135, statusY);
            }
            using (Pen divider = new Pen(Color.FromArgb(72, 83, 101, 120), 1f))
                g.DrawLine(divider, 13, 38, 239, 38);

            DrawQuotaColumn(g, new Rectangle(14, 47, 224, 72), chinese ? "每周" : "Weekly", weekly, usage.SecondaryReset, true, chinese);
        }

        private void DrawQuotaColumn(Graphics g, Rectangle bounds, string label, double remaining, long reset, bool weekly, bool chinese)
        {
            StatePalette palette = PaletteFor(remaining);
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(83, 94, 108))) g.DrawString(label, labelFont, secondary, bounds.X, bounds.Y);
            string value = remaining < 0 ? "—" : Math.Round(remaining).ToString("0") + "%";
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(21, 25, 31))) g.DrawString(value, percentFont, ink, bounds.X - 2, bounds.Y + 13);
            DrawProgress(g, new Rectangle(bounds.X, bounds.Y + 48, bounds.Width, 5), remaining, palette);
            if (weekly)
            {
                string resetText = FormatReset(reset, true, chinese);
                using (SolidBrush secondary = new SolidBrush(Color.FromArgb(94, 103, 115))) g.DrawString(resetText, resetFont, secondary, bounds.X, bounds.Y + 57);
            }
        }

        private static void DrawProgress(Graphics g, Rectangle track, double remaining, StatePalette palette)
        {
            using (GraphicsPath trackPath = RoundedRect(track, track.Height))
            using (SolidBrush trackFill = new SolidBrush(Color.FromArgb(62, 255, 255, 255))) g.FillPath(trackFill, trackPath);
            if (remaining < 0) return;
            int width = Math.Max(2, (int)Math.Round(track.Width * Math.Max(0, Math.Min(100, remaining)) / 100));
            using (GraphicsPath clip = RoundedRect(new Rectangle(track.X, track.Y, width, track.Height), track.Height))
            using (LinearGradientBrush fill = new LinearGradientBrush(track, palette.ProgressStart, palette.ProgressEnd, 0f))
            {
                using (Region previous = g.Clip)
                {
                    g.SetClip(clip);
                    g.FillRectangle(fill, track);
                    g.Clip = previous;
                }
            }
        }

        private static string StatusLabel(double remaining, string dataStatus, bool chinese)
        {
            if (dataStatus == "loading") return chinese ? "同步中" : "Syncing";
            if (dataStatus == "stale") return chinese ? "数据过期" : "Stale";
            if (remaining < 0) return chinese ? "不可用" : "Unavailable";
            if (remaining >= 50) return chinese ? "健康" : "Healthy";
            if (remaining >= 10) return chinese ? "谨慎" : "Caution";
            return chinese ? "紧急" : "Critical";
        }

        private static StatePalette PaletteFor(double remaining)
        {
            if (remaining < 0) return Palette("#D7E0E7", "#F1F4F6", "#D7E0E7", "#7B8490", "#9AA4AE", "#BEC6CD");
            if (remaining >= 50) return Palette("#B9D5EE", "#DFF4E5", "#C7DDF2", "#33C878", "#397AE0", "#91BAF0");
            if (remaining >= 10) return Palette("#B7D0EC", "#FFF0BA", "#F4C979", "#D69B2D", "#4D88D8", "#9FC2EE");
            return Palette("#C4CEE0", "#FFD8A8", "#F07260", "#E95D4F", "#FF7848", "#FFD064");
        }

        private static StatePalette Palette(string cool, string glow, string warm, string signal, string start, string end)
        {
            return new StatePalette {
                Cool = ColorTranslator.FromHtml(cool), Glow = ColorTranslator.FromHtml(glow), Warm = ColorTranslator.FromHtml(warm),
                Signal = ColorTranslator.FromHtml(signal), ProgressStart = ColorTranslator.FromHtml(start), ProgressEnd = ColorTranslator.FromHtml(end)
            };
        }

        private static string FormatReset(long epoch, bool weekly, bool chinese)
        {
            if (epoch <= 0) return chinese ? "重置 —" : "Reset —";
            DateTime value = EpochToLocal(epoch);
            string time = weekly ? value.ToString("M/d HH:mm") : value.ToString("HH:mm");
            return chinese ? "重置 " + time : "Reset " + time;
        }

        private static DateTime EpochToLocal(long epoch)
        {
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch).ToLocalTime(); }
            catch { return DateTime.MinValue; }
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            int diameter = Math.Max(2, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private bool IsOrbSize
        {
            get { return LogicalClientWidth <= 56 && LogicalClientHeight <= 56; }
        }

        internal float CurrentScale
        {
            get { return uiScale; }
        }

        private int LogicalClientWidth
        {
            get { return Math.Max(1, (int)Math.Round(ClientSize.Width / uiScale)); }
        }

        private int LogicalClientHeight
        {
            get { return Math.Max(1, (int)Math.Round(ClientSize.Height / uiScale)); }
        }

        private int Scale(int logical)
        {
            return Math.Max(1, (int)Math.Round(logical * uiScale));
        }

        private Size ScaleSize(Size logical)
        {
            return new Size(Scale(logical.Width), Scale(logical.Height));
        }

        private Rectangle ScaleRectangle(Rectangle logical)
        {
            return new Rectangle(Scale(logical.X), Scale(logical.Y), Scale(logical.Width), Scale(logical.Height));
        }

        private static Font PixelFont(string family, float size, FontStyle style)
        {
            return new Font(family, Math.Max(1f, size), style, GraphicsUnit.Pixel);
        }

        private bool IsQuotaHit(Point physicalPoint)
        {
            if (IsOrbSize || LogicalClientWidth < 210) return false;
            Point logical = new Point((int)Math.Round(physicalPoint.X / uiScale), (int)Math.Round(physicalPoint.Y / uiScale));
            return new Rectangle(10, 40, 232, 88).Contains(logical);
        }

        private void BeginInteractionGrace(TimeSpan duration)
        {
            DateTime candidate = DateTime.UtcNow.Add(duration);
            if (candidate > interactionGraceUntil) interactionGraceUntil = candidate;
        }

        private bool OpenTokenDetails()
        {
            BeginInteractionGrace(TimeSpan.FromSeconds(8));
            try
            {
                if (detailsForm != null && !detailsForm.IsDisposed)
                {
                    detailsForm.SetLanguage(preferences.Language != "en");
                    detailsForm.Reveal();
                    detailsForm.RequestRefresh();
                    SetExpanded(false, false);
                    return true;
                }

                detailsForm = new TokenDetailsForm(sessionsRoot, historyCachePath, preferences.Language != "en", uiScale);
                detailsForm.FormClosed += delegate { detailsForm = null; };
                Screen screen = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2));
                detailsForm.ShowOnScreen(screen);
                SetExpanded(false, false);
                return true;
            }
            catch
            {
                if (detailsForm != null && detailsForm.IsDisposed) detailsForm = null;
                return false;
            }
        }

        private void ExpandFromClick()
        {
            DateTime now = DateTime.UtcNow;
            BeginInteractionGrace(TimeSpan.FromSeconds(4));
            lastPointerInside = now;
            SetExpanded(true, false);
            if (usage.SampleUtc == DateTime.MinValue || now - usage.SampleUtc > TimeSpan.FromSeconds(5))
                StartRefresh(true);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            BeginInteractionGrace(TimeSpan.FromSeconds(3));
            dragCandidate = true;
            dragMoved = false;
            dragStartScreen = Cursor.Position;
            dragStartAnchor = ResolveOrbAnchor();
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragCandidate)
            {
                Point current = Cursor.Position;
                int dx = current.X - dragStartScreen.X;
                int dy = current.Y - dragStartScreen.Y;
                if (!dragMoved && Math.Abs(dx) + Math.Abs(dy) >= Scale(4))
                {
                    dragMoved = true;
                    dragging = true;
                    preferences.HasCustomPosition = true;
                }
                if (dragMoved)
                {
                    preferences.X = dragStartAnchor.X + dx;
                    preferences.Y = dragStartAnchor.Y + dy;
                    lastPointerInside = DateTime.UtcNow;
                    ApplyWindowBounds(ClientSize);
                    ApplyZOrder();
                }
                return;
            }
            Cursor = IsOrbSize || IsQuotaHit(e.Location) ? Cursors.Hand : Cursors.SizeAll;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !dragCandidate) return;
            dragCandidate = false;
            Capture = false;
            bool moved = dragMoved;
            dragMoved = false;
            dragging = false;
            if (moved)
            {
                ApplyWindowBounds(ClientSize);
                PreferencesStore.Write(preferencesPath, preferences);
            }
            else if (IsQuotaHit(e.Location))
            {
                OpenTokenDetails();
            }
            else if (IsOrbSize)
            {
                ExpandFromClick();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragging) Cursor = Cursors.Default;
        }

        protected override void WndProc(ref Message message)
        {
            const int WmDpiChanged = 0x02E0;
            if (message.Msg == WmDpiChanged)
            {
                int dpi = message.WParam.ToInt32() & 0xffff;
                if (dpi > 0) ApplyDpi(dpi / 96f);
            }
            base.WndProc(ref message);
        }

        private void ApplyDpi(float nextScale)
        {
            if (Math.Abs(nextScale - uiScale) < 0.01f) return;
            uiScale = Math.Max(1f, nextScale);
            Font languageFont = languageButton.Font;
            Font topFont = topButton.Font;
            languageButton.Font = PixelFont("Segoe UI", 9.6f * uiScale, FontStyle.Bold);
            topButton.Font = PixelFont("Segoe UI", 9.6f * uiScale, FontStyle.Bold);
            if (languageFont != null) languageFont.Dispose();
            if (topFont != null) topFont.Dispose();
            ApplyWindowBounds(ScaleSize(expandedTarget ? CardLogicalSize : OrbLogicalSize));
            ApplyRegion();
            RefreshVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.L) { ToggleLanguage(); e.Handled = true; }
            else if (e.KeyCode == Keys.T) { ToggleAlwaysOnTop(); e.Handled = true; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && expandedTarget)
            {
                OpenTokenDetails();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                if (expandedTarget) SetExpanded(false, false);
                else { userClosing = true; Close(); }
                e.Handled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!userClosing && e.CloseReason == CloseReason.UserClosing) userClosing = true;
            monitorTimer.Stop(); animationTimer.Stop();
            if (detailsForm != null && !detailsForm.IsDisposed) detailsForm.Close();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (Context != null) Context.ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (sessionWatcher != null) sessionWatcher.Dispose();
                monitorTimer.Dispose(); animationTimer.Dispose(); toolTip.Dispose();
                headerFont.Dispose(); statusFont.Dispose(); labelFont.Dispose(); percentFont.Dispose(); resetFont.Dispose(); orbFont.Dispose(); orbPercentFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public static class Runtime
    {
        [STAThread]
        public static void Run(string sessionsRoot, string statePath, int autoCloseSeconds, bool forceVisible)
        {
            NativeMethods.InitializeDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            ApplicationContext context = new ApplicationContext();
            QuotaMonitorForm form = new QuotaMonitorForm(sessionsRoot, statePath, autoCloseSeconds, forceVisible);
            form.Context = context;
            form.StartMonitor();
            Application.Run(context);
        }

        [STAThread]
        public static void RunDetails(string sessionsRoot, string statePath, int autoCloseSeconds)
        {
            NativeMethods.InitializeDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string folder = Path.GetDirectoryName(statePath);
            WidgetPreferences preferences = PreferencesStore.Read(Path.Combine(folder, "preferences.json"));
            TokenDetailsForm detail = new TokenDetailsForm(
                sessionsRoot,
                Path.Combine(folder, "token-history-cache.json"),
                preferences.Language != "en",
                NativeMethods.SystemScale());
            detail.PlaceOnScreen(Screen.PrimaryScreen);
            if (autoCloseSeconds > 0)
            {
                Timer closeTimer = new Timer { Interval = autoCloseSeconds * 1000 };
                closeTimer.Tick += delegate { closeTimer.Stop(); closeTimer.Dispose(); detail.Close(); };
                closeTimer.Start();
            }
            Application.Run(detail);
        }
    }
}
