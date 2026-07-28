using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CodexMonitor
{
    internal enum TokenDetailView
    {
        Daily,
        Weekly,
        Cumulative
    }

    internal sealed class HeatCellHit
    {
        internal RectangleF Bounds;
        internal DateTime Day;
        internal long Tokens;
    }

    internal sealed class TokenDetailsForm : Form
    {
        private static readonly Size LogicalDefaultSize = new Size(1120, 780);
        private static readonly Size LogicalMinimumSize = new Size(900, 690);
        private readonly string sessionsRoot;
        private readonly string cachePath;
        private readonly Timer refreshTimer;
        private readonly ToolTip toolTip;
        private readonly Button dailyButton;
        private readonly Button weeklyButton;
        private readonly Button cumulativeButton;
        private readonly Button retryButton;
        private readonly List<HeatCellHit> heatCells = new List<HeatCellHit>();
        private Task<TokenHistorySnapshot> refreshTask;
        private TokenHistorySnapshot snapshot;
        private ContextBreakdownForm contextBreakdownForm;
        private Rectangle contextPanelBounds;
        private TokenDetailView activeView = TokenDetailView.Daily;
        private DateTime lastRefreshStarted = DateTime.MinValue;
        private bool refreshRequested = true;
        private bool closing;
        private bool chinese;
        private int progressCurrent;
        private int progressTotal;
        private float uiScale = 1f;

        private readonly Font titleFont = PixelFont("Microsoft YaHei UI", 28f, FontStyle.Bold);
        private readonly Font subtitleFont = PixelFont("Microsoft YaHei UI", 12.5f, FontStyle.Regular);
        private readonly Font metricLabelFont = PixelFont("Microsoft YaHei UI", 12f, FontStyle.Bold);
        private readonly Font heroMetricFont = PixelFont("Microsoft YaHei UI", 34f, FontStyle.Regular);
        private readonly Font metricFont = PixelFont("Microsoft YaHei UI", 29f, FontStyle.Regular);
        private readonly Font unitFont = PixelFont("Microsoft YaHei UI", 15f, FontStyle.Bold);
        private readonly Font metaFont = PixelFont("Microsoft YaHei UI", 11f, FontStyle.Regular);
        private readonly Font sectionFont = PixelFont("Microsoft YaHei UI", 17f, FontStyle.Bold);
        private readonly Font chartLabelFont = PixelFont("Microsoft YaHei UI", 10.5f, FontStyle.Regular);
        private readonly Font chartValueFont = PixelFont("Consolas", 10f, FontStyle.Bold);
        private readonly Font emptyFont = PixelFont("Microsoft YaHei UI", 15f, FontStyle.Bold);

        internal TokenDetailsForm(string sessionsRootValue, string cachePathValue, bool chineseValue, float initialScale)
        {
            sessionsRoot = sessionsRootValue;
            cachePath = cachePathValue;
            chinese = chineseValue;
            uiScale = Math.Max(1f, initialScale);

            Text = chinese ? "Codex Token 使用详情" : "Codex token usage details";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(238, 244, 249);
            ClientSize = ScaleSize(LogicalDefaultSize);
            MinimumSize = ScaleSize(LogicalMinimumSize);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            dailyButton = CreateTabButton();
            weeklyButton = CreateTabButton();
            cumulativeButton = CreateTabButton();
            retryButton = CreateTabButton();
            dailyButton.Click += delegate { SetView(TokenDetailView.Daily); };
            weeklyButton.Click += delegate { SetView(TokenDetailView.Weekly); };
            cumulativeButton.Click += delegate { SetView(TokenDetailView.Cumulative); };
            retryButton.Click += delegate { RequestRefresh(true); };
            Controls.Add(dailyButton);
            Controls.Add(weeklyButton);
            Controls.Add(cumulativeButton);
            Controls.Add(retryButton);

            toolTip = new ToolTip { InitialDelay = 150, ReshowDelay = 80, AutoPopDelay = 5000 };
            refreshTimer = new Timer { Interval = 250 };
            refreshTimer.Tick += RefreshTick;
            UpdateLanguage();
            LayoutControls();
        }

        internal void ShowOnScreen(Screen screen)
        {
            PlaceOnScreen(screen);
            Reveal();
        }

        internal void Reveal()
        {
            if (!Visible) Show();
            bool restoreTopMost = TopMost;
            TopMost = true;
            BringToFront();
            NativeMethods.SetForegroundWindow(Handle);
            Activate();
            BeginInvoke(new MethodInvoker(delegate
            {
                if (IsDisposed) return;
                TopMost = restoreTopMost;
                BringToFront();
                NativeMethods.SetForegroundWindow(Handle);
                Activate();
            }));
        }

        internal void PlaceOnScreen(Screen screen)
        {
            Rectangle work = screen == null ? Screen.PrimaryScreen.WorkingArea : screen.WorkingArea;
            Size size = ScaleSize(LogicalDefaultSize);
            int width = Math.Min(size.Width, Math.Max(Scale(780), work.Width - Scale(48)));
            int height = Math.Min(size.Height, Math.Max(Scale(650), work.Height - Scale(48)));
            Bounds = new Rectangle(
                work.Left + Math.Max(Scale(24), (work.Width - width) / 2),
                work.Top + Math.Max(Scale(18), (work.Height - height) / 2 - Scale(12)),
                width,
                height);
        }

        internal void SetLanguage(bool chineseValue)
        {
            chinese = chineseValue;
            UpdateLanguage();
            if (contextBreakdownForm != null && !contextBreakdownForm.IsDisposed)
                contextBreakdownForm.SetLanguage(chineseValue);
            Invalidate();
        }

        internal void RequestRefresh()
        {
            RequestRefresh(false);
        }

        private void RequestRefresh(bool force)
        {
            refreshRequested = true;
            if (force) lastRefreshStarted = DateTime.MinValue;
            if (Visible && refreshTask == null) StartRefresh();
        }

        private Button CreateTabButton()
        {
            Button button = new Button();
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.TabStop = true;
            button.Cursor = Cursors.Hand;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Padding = new Padding(0);
            return button;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            refreshTimer.Start();
            StartRefresh();
        }

        private void RefreshTick(object sender, EventArgs args)
        {
            if (refreshTask != null && refreshTask.IsCompleted)
            {
                try { snapshot = refreshTask.Result; }
                catch { snapshot = new TokenHistorySnapshot { Status = "unavailable", Message = "Local Codex token history could not be read.", SampleUtc = DateTime.UtcNow }; }
                refreshTask = null;
                progressCurrent = 0;
                progressTotal = 0;
                retryButton.Visible = snapshot == null || snapshot.Status == "unavailable";
                if (contextBreakdownForm != null && !contextBreakdownForm.IsDisposed)
                    contextBreakdownForm.UpdateHistory(snapshot);
                Invalidate();
            }

            if (refreshTask == null && (refreshRequested || DateTime.UtcNow - lastRefreshStarted >= TimeSpan.FromSeconds(10)))
                StartRefresh();
        }

        private void StartRefresh()
        {
            if (refreshTask != null || DateTime.UtcNow - lastRefreshStarted < TimeSpan.FromSeconds(1)) return;
            refreshRequested = false;
            lastRefreshStarted = DateTime.UtcNow;
            progressCurrent = 0;
            progressTotal = 0;
            retryButton.Visible = false;
            Action<int, int> progress = delegate(int current, int total)
            {
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        progressCurrent = current;
                        progressTotal = total;
                        Invalidate();
                    });
                }
                catch { }
            };
            refreshTask = Task.Factory.StartNew<TokenHistorySnapshot>(delegate {
                return TokenHistoryReader.ReadLatest(sessionsRoot, cachePath, progress);
            });
            Invalidate();
        }

        private void SetView(TokenDetailView value)
        {
            activeView = value;
            UpdateTabStyles();
            Invalidate();
        }

        private void UpdateLanguage()
        {
            Text = chinese ? "Codex Token 使用详情" : "Codex token usage details";
            dailyButton.Text = chinese ? "每日" : "Daily";
            weeklyButton.Text = chinese ? "每周" : "Weekly";
            cumulativeButton.Text = chinese ? "累计" : "Cumulative";
            retryButton.Text = chinese ? "重新整理" : "Retry";
            dailyButton.AccessibleName = chinese ? "按日查看 Token 活动" : "View daily token activity";
            weeklyButton.AccessibleName = chinese ? "按周查看 Token 趋势" : "View weekly token trend";
            cumulativeButton.AccessibleName = chinese ? "查看累计 Token 趋势" : "View cumulative token trend";
            retryButton.AccessibleName = chinese ? "重新读取本机 Codex Token 记录" : "Read local Codex token history again";
            UpdateTabStyles();
        }

        private void UpdateTabStyles()
        {
            StyleTab(dailyButton, activeView == TokenDetailView.Daily);
            StyleTab(weeklyButton, activeView == TokenDetailView.Weekly);
            StyleTab(cumulativeButton, activeView == TokenDetailView.Cumulative);
            retryButton.BackColor = Color.FromArgb(226, 235, 242);
            retryButton.ForeColor = Color.FromArgb(49, 61, 74);
            Font previous = retryButton.Font;
            retryButton.Font = PixelFont("Microsoft YaHei UI", 11f * uiScale, FontStyle.Bold);
            if (previous != null && !Object.ReferenceEquals(previous, Control.DefaultFont)) previous.Dispose();
        }

        private void StyleTab(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(57, 122, 224) : Color.FromArgb(232, 239, 245);
            button.ForeColor = selected ? Color.FromArgb(248, 251, 253) : Color.FromArgb(61, 72, 86);
            Font previous = button.Font;
            button.Font = PixelFont("Microsoft YaHei UI", 11f * uiScale, selected ? FontStyle.Bold : FontStyle.Regular);
            if (previous != null && !Object.ReferenceEquals(previous, Control.DefaultFont)) previous.Dispose();
        }

        private void LayoutControls()
        {
            if (dailyButton == null || weeklyButton == null || cumulativeButton == null || retryButton == null) return;
            int logicalWidth = LogicalClientWidth;
            int tabWidth = chinese ? 64 : 88;
            int gap = 6;
            int total = tabWidth * 3 + gap * 2;
            int start = Math.Max(460, logicalWidth - 40 - total);
            SetControlBounds(dailyButton, new Rectangle(start, 456, tabWidth, 36));
            SetControlBounds(weeklyButton, new Rectangle(start + tabWidth + gap, 456, tabWidth, 36));
            SetControlBounds(cumulativeButton, new Rectangle(start + (tabWidth + gap) * 2, 456, tabWidth, 36));
            SetControlBounds(retryButton, new Rectangle(Math.Max(40, (logicalWidth - 112) / 2), 625, 112, 40));
            retryButton.Visible = snapshot != null && snapshot.Status == "unavailable";
        }

        private void SetControlBounds(Control control, Rectangle logical)
        {
            control.Bounds = new Rectangle(Scale(logical.X), Scale(logical.Y), Scale(logical.Width), Scale(logical.Height));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutControls();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            e.Graphics.ScaleTransform(uiScale, uiScale);
            DrawSurface(e.Graphics);
        }

        private void DrawSurface(Graphics g)
        {
            int width = LogicalClientWidth;
            int height = LogicalClientHeight;
            g.Clear(Color.FromArgb(238, 244, 249));
            using (LinearGradientBrush wash = new LinearGradientBrush(
                new Rectangle(0, 0, width, Math.Max(1, height)),
                Color.FromArgb(230, 242, 250), Color.FromArgb(247, 249, 250), 15f))
                g.FillRectangle(wash, 0, 0, width, height);

            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            {
                g.DrawString(chinese ? "Token 使用详情" : "Token usage details", titleFont, ink, 40, 34);
                g.DrawString(
                    chinese ? "本月与累计 Token 按本机历史统计；官方周配额仅显示在浮球" : "Monthly and cumulative Tokens use local history; official weekly quota remains in the orb",
                    subtitleFont, secondary, 40, 75);
            }

            DrawMetricRow(g, width);
            DrawContextPanel(g, width);
            DrawActivityPanel(g, width, height);
        }

        private void DrawMetricRow(Graphics g, int width)
        {
            const int x = 40;
            const int y = 112;
            const int gap = 14;
            const int height = 128;
            int available = Math.Max(720, width - 80);
            int heroWidth = Math.Max(228, (int)Math.Round(available * 0.27));
            int secondaryWidth = Math.Max(168, (available - heroWidth - gap * 3) / 3);
            int lastWidth = available - heroWidth - secondaryWidth * 2 - gap * 3;
            long total = snapshot == null ? -1 : snapshot.TotalTokens;
            long today = snapshot == null ? -1 : snapshot.TodayTokens;
            long month = snapshot == null ? -1 : snapshot.MonthTokens;
            long week = snapshot == null ? -1 : snapshot.WeekTokens;
            string since = snapshot == null || snapshot.SinceLocal == DateTime.MinValue
                ? (chinese ? "正在读取本机记录" : "Reading local history")
                : (chinese ? "自 " : "Since ") + snapshot.SinceLocal.ToString(chinese ? "yyyy.M.d" : "MMM d, yyyy", CultureInfo.CurrentCulture);

            DrawMetricCard(g, new Rectangle(x, y, heroWidth, height), chinese ? "本机记录累计" : "Local history total", total, since, true);
            DrawMetricCard(g, new Rectangle(x + heroWidth + gap, y, secondaryWidth, height), chinese ? "今日消耗" : "Today", today, chinese ? "按本地自然日统计" : "Local calendar day", false);
            DrawMetricCard(g, new Rectangle(x + heroWidth + gap + secondaryWidth + gap, y, secondaryWidth, height), chinese ? "本月" : "This month", month, chinese ? "按当前自然月统计" : "Current calendar month", false);
            DrawMetricCard(g, new Rectangle(x + heroWidth + gap + (secondaryWidth + gap) * 2, y, lastWidth, height), chinese ? "本周" : "This week", week, chinese ? "周一至今日" : "Monday through today", false);
        }

        private void DrawMetricCard(Graphics g, Rectangle bounds, string label, long value, string meta, bool primary)
        {
            Rectangle shadowBounds = new Rectangle(bounds.X, bounds.Y + 3, bounds.Width, bounds.Height);
            using (GraphicsPath shadowPath = RoundedRect(shadowBounds, 16))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(24, 66, 88, 108)))
                g.FillPath(shadow, shadowPath);
            using (GraphicsPath path = RoundedRect(bounds, primary ? 18 : 14))
            using (LinearGradientBrush fill = new LinearGradientBrush(
                bounds,
                primary ? Color.FromArgb(226, 242, 252) : Color.FromArgb(249, 251, 252),
                primary ? Color.FromArgb(240, 248, 244) : Color.FromArgb(246, 249, 251),
                primary ? 145f : 90f))
                g.FillPath(fill, path);
            using (GraphicsPath edgePath = RoundedRect(new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1), primary ? 18 : 14))
            using (Pen edge = new Pen(Color.FromArgb(primary ? 64 : 42, 102, 128, 151), 1f))
                g.DrawPath(edge, edgePath);

            using (SolidBrush labelBrush = new SolidBrush(Color.FromArgb(57, 70, 84)))
            using (SolidBrush valueBrush = new SolidBrush(primary ? Color.FromArgb(42, 100, 183) : Color.FromArgb(20, 26, 33)))
            using (SolidBrush metaBrush = new SolidBrush(Color.FromArgb(82, 94, 107)))
            {
                g.DrawString(label, metricLabelFont, labelBrush, bounds.X + 20, bounds.Y + 18);
                DrawMetricValue(g, value < 0 ? "—" : FormatTokens(value, chinese), primary ? heroMetricFont : metricFont, valueBrush, bounds.X + 18, bounds.Y + 47);
                g.DrawString(meta, metaFont, metaBrush, bounds.X + 20, bounds.Bottom - 27);
            }
        }

        private void DrawMetricValue(Graphics g, string formatted, Font font, Brush brush, float x, float y)
        {
            if (!chinese)
            {
                g.DrawString(formatted, font, brush, x, y);
                return;
            }
            int split = formatted.LastIndexOf(' ');
            if (split <= 0 || split >= formatted.Length - 1)
            {
                g.DrawString(formatted, font, brush, x, y);
                return;
            }
            string number = formatted.Substring(0, split);
            string unit = formatted.Substring(split + 1);
            g.DrawString(number, font, brush, x, y);
            float numberWidth = g.MeasureString(number, font).Width;
            g.DrawString(unit, unitFont, brush, x + numberWidth + 2, y + 14);
        }

        private void DrawContextPanel(Graphics g, int width)
        {
            Rectangle panel = new Rectangle(40, 260, Math.Max(720, width - 80), 154);
            contextPanelBounds = panel;
            using (GraphicsPath path = RoundedRect(panel, 20))
            using (LinearGradientBrush fill = new LinearGradientBrush(
                panel, Color.FromArgb(239, 248, 253), Color.FromArgb(247, 250, 246), 8f))
                g.FillPath(fill, path);
            using (GraphicsPath edgePath = RoundedRect(new Rectangle(panel.X, panel.Y, panel.Width - 1, panel.Height - 1), 20))
            using (Pen edge = new Pen(Color.FromArgb(58, 93, 127, 152), 1f))
                g.DrawPath(edge, edgePath);

            bool available = snapshot != null && snapshot.Available && snapshot.TotalTokens > 0 && snapshot.MonthTokens >= 0;
            double monthShare = available ? Share(snapshot.MonthTokens, snapshot.TotalTokens) : -1;
            ConversationTokenUsage activeConversation = FindActiveConversation(snapshot);
            ProjectTokenUsage activeProject = FindActiveProject(snapshot, activeConversation);
            double projectShare = activeProject == null || snapshot == null ? -1 : Share(activeProject.Tokens, snapshot.TotalTokens);
            double conversationShare = activeConversation == null || snapshot == null ? -1 : Share(activeConversation.Tokens, snapshot.TotalTokens);
            double conversationProjectShare = activeConversation == null || activeProject == null
                ? -1
                : Share(activeConversation.Tokens, activeProject.Tokens);
            Color state = Color.FromArgb(57, 122, 224);
            int heroWidth = Math.Max(260, (int)Math.Round(panel.Width * 0.34));

            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            using (SolidBrush stateBrush = new SolidBrush(state))
            using (Pen divider = new Pen(Color.FromArgb(35, 86, 105, 122), 1f))
            {
                g.DrawString(chinese ? "本月总用量" : "Monthly total usage", sectionFont, ink, panel.X + 24, panel.Y + 17);
                string action = chinese ? "点击查看容量与占用明细" : "Click for capacity and usage details";
                SizeF actionSize = g.MeasureString(action, metaFont);
                g.DrawString(action, metaFont, secondary, panel.Right - 24 - actionSize.Width, panel.Y + 20);
                string monthValue = available ? FormatTokens(snapshot.MonthTokens, chinese) : "—";
                DrawMetricValue(g, monthValue, heroMetricFont, stateBrush, panel.X + 22, panel.Y + 50);
                string ratio = available
                    ? (chinese ? "占累计总量 " : "Share of cumulative total ") + FormatPercent(monthShare)
                    : (chinese ? "正在读取本机 Token 历史" : "Reading local Token history");
                g.DrawString(ratio, metaFont, secondary, panel.X + 24, panel.Y + 91);
                g.DrawString(chinese ? "本月占累计" : "Month / cumulative", metaFont, secondary, panel.X + 24, panel.Y + 110);

                Rectangle track = new Rectangle(panel.X + 105, panel.Y + 113, Math.Max(110, heroWidth - 129), 7);
                using (GraphicsPath trackPath = RoundedRect(track, 4))
                using (SolidBrush trackFill = new SolidBrush(Color.FromArgb(220, 229, 235)))
                    g.FillPath(trackFill, trackPath);
                if (available)
                {
                    int fillWidth = Math.Max(4, (int)Math.Round(track.Width * monthShare / 100d));
                    using (GraphicsPath fillPath = RoundedRect(new Rectangle(track.X, track.Y, Math.Min(track.Width, fillWidth), track.Height), 4))
                    using (SolidBrush progress = new SolidBrush(state))
                        g.FillPath(progress, fillPath);
                }
                g.DrawString(
                    chinese ? "按当前自然月统计" : "Current calendar month",
                    metaFont, secondary, panel.X + 24, panel.Y + 132);

                int detailsX = panel.X + heroWidth + 18;
                int detailsWidth = panel.Right - 24 - detailsX;
                g.DrawLine(divider, detailsX - 12, panel.Y + 24, detailsX - 12, panel.Bottom - 23);
                int cellWidth = Math.Max(90, detailsWidth / 4);
                int cellY = panel.Y + 52;
                DrawContextValue(g, detailsX, cellY, cellWidth,
                    chinese ? "当前项目总占比" : "Current project / total",
                    FormatShare(projectShare));
                DrawContextValue(g, detailsX + cellWidth, cellY, cellWidth,
                    chinese ? "当前对话总占比" : "Current conversation / total",
                    FormatShare(conversationShare));
                DrawContextValue(g, detailsX + cellWidth * 2, cellY, cellWidth,
                    chinese ? "项目内占比" : "Conversation / project",
                    FormatShare(conversationProjectShare));
                DrawContextValue(g, detailsX + cellWidth * 3, cellY, Math.Max(80, detailsWidth - cellWidth * 3),
                    chinese ? "本机累计" : "Local cumulative",
                    snapshot == null ? "—" : FormatTokens(snapshot.TotalTokens, chinese));

                string footer = available
                    ? (chinese ? "本月 " : "Month ") + FormatTokens(snapshot.MonthTokens, chinese)
                        + (chinese ? " / 本机累计 " : " / local cumulative ") + FormatTokens(snapshot.TotalTokens, chinese)
                        + (chinese ? " · 本机数字记录统计" : " · local numeric history")
                    : (chinese ? "本机 Token 历史暂不可用，将自动重试" : "Local Token history is unavailable; retrying automatically");
                g.DrawString(footer, metaFont, secondary, detailsX, panel.Bottom - 30);
            }
        }

        private static double ClampPercent(double value)
        {
            return Math.Max(0d, Math.Min(100d, value));
        }

        private static string FormatPercent(double value)
        {
            return ClampPercent(value).ToString("0", CultureInfo.CurrentCulture) + "%";
        }

        private static string FormatShare(double value)
        {
            return value < 0 ? "—" : FormatPercent(value);
        }

        private static double Share(long part, long total)
        {
            if (total <= 0 || part < 0) return -1;
            return ClampPercent(part * 100d / total);
        }

        private static ConversationTokenUsage FindActiveConversation(TokenHistorySnapshot value)
        {
            if (value == null || value.Context == null || String.IsNullOrWhiteSpace(value.Context.SessionId)) return null;
            foreach (ConversationTokenUsage conversation in value.Conversations)
            {
                if (conversation != null && String.Equals(conversation.SessionId, value.Context.SessionId, StringComparison.OrdinalIgnoreCase))
                    return conversation;
            }
            return null;
        }

        private static ProjectTokenUsage FindActiveProject(TokenHistorySnapshot value, ConversationTokenUsage conversation)
        {
            if (value == null || conversation == null) return null;
            foreach (ProjectTokenUsage project in value.Projects)
            {
                if (project == null) continue;
                if (!String.IsNullOrWhiteSpace(conversation.ProjectPath)
                    && String.Equals(project.ProjectPath, conversation.ProjectPath, StringComparison.OrdinalIgnoreCase))
                    return project;
                if (String.IsNullOrWhiteSpace(conversation.ProjectPath)
                    && String.Equals(project.ProjectName, conversation.ProjectName, StringComparison.OrdinalIgnoreCase))
                    return project;
            }
            return null;
        }

        private void DrawContextValue(Graphics g, int x, int y, int width, string label, string value)
        {
            using (SolidBrush labelBrush = new SolidBrush(Color.FromArgb(82, 94, 107)))
            using (SolidBrush valueBrush = new SolidBrush(Color.FromArgb(20, 26, 33)))
            {
                g.DrawString(label, metaFont, labelBrush, new RectangleF(x, y, width - 8, 18));
                g.DrawString(value, metricLabelFont, valueBrush, new RectangleF(x, y + 25, width - 8, 24));
            }
        }

        private void DrawActivityPanel(Graphics g, int width, int height)
        {
            Rectangle panel = new Rectangle(40, 438, Math.Max(720, width - 80), Math.Max(220, height - 478));
            using (GraphicsPath path = RoundedRect(panel, 20))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(248, 251, 252)))
                g.FillPath(fill, path);
            using (GraphicsPath edgePath = RoundedRect(new Rectangle(panel.X, panel.Y, panel.Width - 1, panel.Height - 1), 20))
            using (Pen edge = new Pen(Color.FromArgb(40, 95, 118, 138), 1f))
                g.DrawPath(edge, edgePath);

            using (SolidBrush ink = new SolidBrush(Color.FromArgb(26, 32, 40)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(82, 94, 107)))
            {
                g.DrawString(chinese ? "Token 活动" : "Token activity", sectionFont, ink, panel.X + 24, panel.Y + 22);
                string status = ActivityStatus();
                SizeF statusSize = g.MeasureString(status, metaFont);
                g.DrawString(status, metaFont, secondary, panel.Right - 24 - statusSize.Width, panel.Y + 61);
            }

            if (snapshot == null)
            {
                DrawLoading(g, panel);
                return;
            }
            if (snapshot.Status == "unavailable")
            {
                DrawEmpty(g, panel, chinese ? "无法读取本机 Codex Token 记录" : "Local Codex token history is unavailable");
                return;
            }
            if (!snapshot.Available || snapshot.Days.Count == 0)
            {
                DrawEmpty(g, panel, chinese ? "尚未找到可统计的 Codex Token 记录" : "No Codex token records are available yet");
                return;
            }

            if (activeView == TokenDetailView.Daily) DrawHeatmap(g, panel);
            else DrawTrend(g, panel, activeView == TokenDetailView.Cumulative);
        }

        private string ActivityStatus()
        {
            if (refreshTask != null && progressTotal > 0)
                return chinese
                    ? "正在整理 " + progressCurrent + " / " + progressTotal + " 个会话"
                    : "Indexing " + progressCurrent + " / " + progressTotal + " sessions";
            if (snapshot == null) return chinese ? "正在读取本机记录" : "Reading local history";
            string time = snapshot.SampleUtc.ToLocalTime().ToString("HH:mm:ss");
            return chinese ? "更新于 " + time : "Updated " + time;
        }

        private void DrawLoading(Graphics g, Rectangle panel)
        {
            int chartTop = panel.Y + 104;
            using (SolidBrush muted = new SolidBrush(Color.FromArgb(224, 233, 240)))
            {
                for (int column = 0; column < 48; column++)
                    for (int row = 0; row < 7; row++)
                        g.FillRectangle(muted, panel.X + 70 + column * 15, chartTop + row * 15, 11, 11);
            }
        }

        private void DrawEmpty(Graphics g, Rectangle panel, string message)
        {
            SizeF size = g.MeasureString(message, emptyFont);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(62, 74, 87)))
                g.DrawString(message, emptyFont, ink, panel.X + (panel.Width - size.Width) / 2f, panel.Y + panel.Height / 2f - 32);
        }

        private void DrawHeatmap(Graphics g, Rectangle panel)
        {
            heatCells.Clear();
            Dictionary<DateTime, long> byDay = new Dictionary<DateTime, long>();
            long max = 0;
            foreach (DailyTokenUsage item in snapshot.Days)
            {
                DateTime key = item.Day.Date;
                byDay[key] = item.Tokens;
                if (item.Tokens > max) max = item.Tokens;
            }

            DateTime today = DateTime.Today;
            int dayOffset = ((int)today.DayOfWeek + 6) % 7;
            DateTime endWeek = today.AddDays(6 - dayOffset);
            DateTime start = endWeek.AddDays(-(53 * 7 - 1));
            float chartX = panel.X + 72;
            float chartY = panel.Y + 106;
            float gap = 3f;
            float availableWidth = panel.Width - 104;
            float cell = Math.Max(7f, Math.Min(12f, (availableWidth - gap * 52) / 53f));
            float step = cell + gap;
            Color[] scale = new[] {
                Color.FromArgb(234, 240, 245),
                Color.FromArgb(205, 225, 244),
                Color.FromArgb(145, 186, 240),
                Color.FromArgb(91, 153, 231),
                Color.FromArgb(57, 122, 224)
            };

            using (SolidBrush label = new SolidBrush(Color.FromArgb(84, 96, 110)))
            {
                string[] weekdays = chinese
                    ? new[] { "一", "二", "三", "四", "五", "六", "日" }
                    : new[] { "M", "T", "W", "T", "F", "S", "S" };
                for (int row = 0; row < 7; row += 2)
                    g.DrawString(weekdays[row], chartLabelFont, label, panel.X + 35, chartY + row * step - 1);

                int previousMonth = -1;
                for (int column = 0; column < 53; column++)
                {
                    DateTime first = start.AddDays(column * 7);
                    if (first.Month == previousMonth) continue;
                    previousMonth = first.Month;
                    g.DrawString(first.ToString(chinese ? "M月" : "MMM", CultureInfo.CurrentCulture), chartLabelFont, label, chartX + column * step, panel.Y + 80);
                }
            }

            for (int column = 0; column < 53; column++)
            {
                for (int row = 0; row < 7; row++)
                {
                    DateTime day = start.AddDays(column * 7 + row);
                    long tokens;
                    byDay.TryGetValue(day, out tokens);
                    int level = HeatLevel(tokens, max);
                    RectangleF cellBounds = new RectangleF(chartX + column * step, chartY + row * step, cell, cell);
                    using (GraphicsPath cellPath = RoundedRect(cellBounds, Math.Min(2.5f, cell / 4f)))
                    using (SolidBrush fill = new SolidBrush(day > today ? Color.FromArgb(243, 247, 249) : scale[level]))
                        g.FillPath(fill, cellPath);
                    if (day <= today) heatCells.Add(new HeatCellHit { Bounds = cellBounds, Day = day, Tokens = tokens });
                }
            }

            float legendY = Math.Min(panel.Bottom - 32, chartY + 7 * step + 28);
            using (SolidBrush label = new SolidBrush(Color.FromArgb(84, 96, 110)))
            {
                g.DrawString(chinese ? "较少" : "Less", chartLabelFont, label, panel.X + 34, legendY - 2);
                float legendX = panel.X + 73;
                for (int index = 0; index < scale.Length; index++)
                {
                    using (GraphicsPath cellPath = RoundedRect(new RectangleF(legendX + index * 18, legendY, 12, 12), 3f))
                    using (SolidBrush fill = new SolidBrush(scale[index]))
                        g.FillPath(fill, cellPath);
                }
                g.DrawString(chinese ? "较多" : "More", chartLabelFont, label, legendX + scale.Length * 18 + 4, legendY - 2);
            }
        }

        private void DrawTrend(Graphics g, Rectangle panel, bool cumulative)
        {
            const int weeks = 26;
            DateTime today = DateTime.Today;
            int mondayOffset = ((int)today.DayOfWeek + 6) % 7;
            DateTime currentWeek = today.AddDays(-mondayOffset);
            DateTime start = currentWeek.AddDays(-(weeks - 1) * 7);
            long[] values = new long[weeks];
            foreach (DailyTokenUsage item in snapshot.Days)
            {
                int index = (int)Math.Floor((item.Day.Date - start).TotalDays / 7d);
                if (index >= 0 && index < weeks) values[index] = SafeAdd(values[index], item.Tokens);
            }
            if (cumulative)
            {
                long carried = 0;
                foreach (DailyTokenUsage item in snapshot.Days)
                    if (item.Day.Date < start) carried = SafeAdd(carried, item.Tokens);
                for (int index = 0; index < weeks; index++)
                {
                    carried = SafeAdd(carried, values[index]);
                    values[index] = carried;
                }
            }

            long max = 1;
            foreach (long value in values) if (value > max) max = value;
            RectangleF chart = new RectangleF(panel.X + 64, panel.Y + 100, panel.Width - 112, panel.Height - 156);
            using (Pen grid = new Pen(Color.FromArgb(28, 83, 103, 121), 1f))
            using (SolidBrush label = new SolidBrush(Color.FromArgb(84, 96, 110)))
            {
                for (int line = 0; line <= 3; line++)
                {
                    float y = chart.Top + chart.Height * line / 3f;
                    g.DrawLine(grid, chart.Left, y, chart.Right, y);
                    long value = (long)Math.Round(max * (3 - line) / 3d);
                    string text = FormatTokens(value, chinese);
                    SizeF size = g.MeasureString(text, chartLabelFont);
                    g.DrawString(text, chartLabelFont, label, chart.Left - size.Width - 10, y - 6);
                }
                for (int index = 0; index < weeks; index += 5)
                {
                    string date = start.AddDays(index * 7).ToString(chinese ? "M/d" : "MMM d", CultureInfo.CurrentCulture);
                    float x = chart.Left + chart.Width * index / (weeks - 1f);
                    g.DrawString(date, chartLabelFont, label, x - 15, chart.Bottom + 12);
                }
            }

            PointF[] points = new PointF[weeks];
            for (int index = 0; index < weeks; index++)
            {
                float x = chart.Left + chart.Width * index / (weeks - 1f);
                float y = chart.Bottom - chart.Height * values[index] / max;
                points[index] = new PointF(x, y);
            }
            GraphicsPath area = new GraphicsPath();
            area.AddLines(points);
            area.AddLine(points[weeks - 1], new PointF(chart.Right, chart.Bottom));
            area.AddLine(new PointF(chart.Right, chart.Bottom), new PointF(chart.Left, chart.Bottom));
            area.CloseFigure();
            using (LinearGradientBrush areaFill = new LinearGradientBrush(
                Rectangle.Round(chart), Color.FromArgb(48, 57, 122, 224), Color.FromArgb(0, 57, 122, 224), 90f))
                g.FillPath(areaFill, area);
            area.Dispose();
            using (Pen line = new Pen(Color.FromArgb(57, 122, 224), 2.2f))
            {
                line.LineJoin = LineJoin.Round;
                g.DrawLines(line, points);
            }
            using (SolidBrush point = new SolidBrush(Color.FromArgb(57, 122, 224)))
                g.FillEllipse(point, points[weeks - 1].X - 4, points[weeks - 1].Y - 4, 8, 8);
            string finalLabel = FormatTokens(values[weeks - 1], chinese);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(42, 100, 183)))
                g.DrawString(finalLabel, chartValueFont, ink, points[weeks - 1].X - g.MeasureString(finalLabel, chartValueFont).Width, Math.Max(chart.Top, points[weeks - 1].Y - 22));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            PointF logical = new PointF(e.X / uiScale, e.Y / uiScale);
            if (contextPanelBounds.Contains(Point.Round(logical)))
            {
                Cursor = Cursors.Hand;
                toolTip.SetToolTip(this, chinese ? "查看容量、项目与对话占用明细" : "View capacity, project, and conversation usage details");
                return;
            }
            Cursor = Cursors.Default;
            if (activeView != TokenDetailView.Daily || heatCells.Count == 0)
            {
                toolTip.SetToolTip(this, null);
                return;
            }
            foreach (HeatCellHit cell in heatCells)
            {
                if (!cell.Bounds.Contains(logical)) continue;
                string text = cell.Day.ToString(chinese ? "yyyy年M月d日" : "MMM d, yyyy", CultureInfo.CurrentCulture)
                    + Environment.NewLine + FormatTokens(cell.Tokens, chinese) + (chinese ? " Token" : " tokens");
                toolTip.SetToolTip(this, text);
                return;
            }
            toolTip.SetToolTip(this, null);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Point logical = new Point((int)Math.Round(e.X / uiScale), (int)Math.Round(e.Y / uiScale));
            if (contextPanelBounds.Contains(logical)) OpenContextBreakdown();
        }

        private void OpenContextBreakdown()
        {
            if (contextBreakdownForm != null && !contextBreakdownForm.IsDisposed)
            {
                contextBreakdownForm.UpdateHistory(snapshot);
                contextBreakdownForm.SetLanguage(chinese);
                contextBreakdownForm.Reveal();
                return;
            }
            contextBreakdownForm = new ContextBreakdownForm(snapshot, chinese, uiScale);
            contextBreakdownForm.FormClosed += delegate
            {
                contextBreakdownForm = null;
                if (!closing && !IsDisposed)
                {
                    Reveal();
                    Focus();
                }
            };
            contextBreakdownForm.ShowFor(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.R)) { RequestRefresh(true); e.Handled = true; }
            else if (e.KeyCode == Keys.Left) { SetView((TokenDetailView)Math.Max(0, (int)activeView - 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SetView((TokenDetailView)Math.Min(2, (int)activeView + 1)); e.Handled = true; }
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
            MinimumSize = ScaleSize(LogicalMinimumSize);
            UpdateTabStyles();
            LayoutControls();
            Invalidate();
        }

        private int LogicalClientWidth { get { return Math.Max(1, (int)Math.Round(ClientSize.Width / uiScale)); } }
        private int LogicalClientHeight { get { return Math.Max(1, (int)Math.Round(ClientSize.Height / uiScale)); } }
        private int Scale(int logical) { return Math.Max(1, (int)Math.Round(logical * uiScale)); }
        private Size ScaleSize(Size logical) { return new Size(Scale(logical.Width), Scale(logical.Height)); }

        private static Font PixelFont(string family, float size, FontStyle style)
        {
            return new Font(family, Math.Max(1f, size), style, GraphicsUnit.Pixel);
        }

        private static string FormatTokens(long value, bool chinese)
        {
            if (value < 0) return "—";
            if (chinese)
            {
                if (value >= 100000000) return (value / 100000000d).ToString(value >= 1000000000 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " 亿";
                if (value >= 10000) return (value / 10000d).ToString(value >= 1000000 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " 万";
                return value.ToString("N0", CultureInfo.CurrentCulture);
            }
            if (value >= 1000000000) return (value / 1000000000d).ToString("0.00", CultureInfo.InvariantCulture) + "B";
            if (value >= 1000000) return (value / 1000000d).ToString("0.00", CultureInfo.InvariantCulture) + "M";
            if (value >= 1000) return (value / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static int HeatLevel(long value, long max)
        {
            if (value <= 0 || max <= 0) return 0;
            double normalized = Math.Log10(value + 1d) / Math.Log10(max + 1d);
            return Math.Max(1, Math.Min(4, (int)Math.Ceiling(normalized * 4d)));
        }

        private static long SafeAdd(long left, long right)
        {
            if (right > 0 && left > Int64.MaxValue - right) return Int64.MaxValue;
            return left + right;
        }

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
        {
            return RoundedRect(new RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), radius);
        }

        private static GraphicsPath RoundedRect(RectangleF bounds, float radius)
        {
            float diameter = Math.Max(2f, Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2f));
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            closing = true;
            refreshTimer.Stop();
            if (contextBreakdownForm != null && !contextBreakdownForm.IsDisposed)
                contextBreakdownForm.Close();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                toolTip.Dispose();
                titleFont.Dispose();
                subtitleFont.Dispose();
                metricLabelFont.Dispose();
                heroMetricFont.Dispose();
                metricFont.Dispose();
                unitFont.Dispose();
                metaFont.Dispose();
                sectionFont.Dispose();
                chartLabelFont.Dispose();
                chartValueFont.Dispose();
                emptyFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
