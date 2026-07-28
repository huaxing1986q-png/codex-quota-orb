using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace CodexMonitor
{
    internal enum ContextBreakdownView
    {
        Capacity,
        Projects,
        Conversations
    }

    internal sealed class ContextBreakdownForm : Form
    {
        private static readonly Size LogicalDefaultSize = new Size(900, 650);
        private static readonly Size LogicalMinimumSize = new Size(760, 560);
        private readonly Button backButton;
        private readonly Button capacityButton;
        private readonly Button projectsButton;
        private readonly Button conversationsButton;
        private readonly ListView projectsList;
        private readonly ListView conversationsList;
        private TokenHistorySnapshot history;
        private ContextBreakdownView activeView = ContextBreakdownView.Capacity;
        private bool chinese;
        private float uiScale;

        private readonly Font titleFont = PixelFont("Microsoft YaHei UI", 25f, FontStyle.Bold);
        private readonly Font subtitleFont = PixelFont("Microsoft YaHei UI", 11.5f, FontStyle.Regular);
        private readonly Font sectionFont = PixelFont("Microsoft YaHei UI", 16f, FontStyle.Bold);
        private readonly Font percentFont = PixelFont("Consolas", 31f, FontStyle.Bold);
        private readonly Font valueFont = PixelFont("Microsoft YaHei UI", 16f, FontStyle.Bold);
        private readonly Font labelFont = PixelFont("Microsoft YaHei UI", 11f, FontStyle.Regular);
        private readonly Font metaFont = PixelFont("Microsoft YaHei UI", 10f, FontStyle.Regular);

        internal ContextBreakdownForm(TokenHistorySnapshot initial, bool chineseValue, float initialScale)
        {
            history = initial ?? new TokenHistorySnapshot();
            chinese = chineseValue;
            uiScale = Math.Max(1f, initialScale);
            Text = chinese ? "上下文总占用明细" : "Aggregate context occupancy details";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(238, 244, 249);
            ClientSize = ScaleSize(LogicalDefaultSize);
            MinimumSize = ScaleSize(LogicalMinimumSize);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            backButton = new Button();
            backButton.FlatStyle = FlatStyle.Flat;
            backButton.FlatAppearance.BorderSize = 1;
            backButton.FlatAppearance.BorderColor = Color.FromArgb(132, 157, 178);
            backButton.BackColor = Color.FromArgb(244, 249, 252);
            backButton.ForeColor = Color.FromArgb(36, 50, 64);
            backButton.Cursor = Cursors.Hand;
            backButton.Click += delegate { Close(); };
            Controls.Add(backButton);

            capacityButton = CreateTabButton();
            projectsButton = CreateTabButton();
            conversationsButton = CreateTabButton();
            capacityButton.Click += delegate { SetView(ContextBreakdownView.Capacity); };
            projectsButton.Click += delegate { SetView(ContextBreakdownView.Projects); };
            conversationsButton.Click += delegate { SetView(ContextBreakdownView.Conversations); };
            Controls.Add(capacityButton);
            Controls.Add(projectsButton);
            Controls.Add(conversationsButton);

            projectsList = CreateListView();
            conversationsList = CreateListView();
            Controls.Add(projectsList);
            Controls.Add(conversationsList);
            UpdateLanguage();
            UpdateLists();
            LayoutControls();
        }

        internal void UpdateHistory(TokenHistorySnapshot value)
        {
            history = value ?? new TokenHistorySnapshot();
            UpdateLists();
            Invalidate();
        }

        internal void SetLanguage(bool chineseValue)
        {
            chinese = chineseValue;
            UpdateLanguage();
            Invalidate();
        }

        internal void ShowFor(Form owner)
        {
            if (!Visible)
            {
                PlaceNear(owner);
                Show(owner);
            }
            Reveal();
        }

        internal void Reveal()
        {
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
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

        private void PlaceNear(Form owner)
        {
            Screen screen = owner == null ? Screen.PrimaryScreen : Screen.FromControl(owner);
            Rectangle work = screen.WorkingArea;
            Size size = ScaleSize(LogicalDefaultSize);
            int width = Math.Min(size.Width, Math.Max(Scale(620), work.Width - Scale(64)));
            int height = Math.Min(size.Height, Math.Max(Scale(460), work.Height - Scale(64)));
            Bounds = new Rectangle(
                work.Left + Math.Max(Scale(32), (work.Width - width) / 2),
                work.Top + Math.Max(Scale(24), (work.Height - height) / 2),
                width,
                height);
        }

        private Button CreateTabButton()
        {
            Button button = new Button();
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.UseVisualStyleBackColor = false;
            button.Cursor = Cursors.Hand;
            button.TextAlign = ContentAlignment.MiddleCenter;
            return button;
        }

        private ListView CreateListView()
        {
            ListView list = new ListView();
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.ShowItemToolTips = true;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Color.FromArgb(248, 251, 252);
            list.ForeColor = Color.FromArgb(29, 38, 48);
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            return list;
        }

        private void SetView(ContextBreakdownView value)
        {
            activeView = value;
            UpdateTabStyles();
            LayoutControls();
            Invalidate();
        }

        private void UpdateLanguage()
        {
            Text = chinese ? "上下文总占用明细" : "Aggregate context occupancy details";
            backButton.Text = chinese ? "返回" : "Back";
            backButton.AccessibleName = chinese ? "返回 Token 使用详情" : "Return to token usage details";
            Font previous = backButton.Font;
            backButton.Font = PixelFont("Microsoft YaHei UI", 10.5f * uiScale, FontStyle.Bold);
            if (previous != null && !Object.ReferenceEquals(previous, Control.DefaultFont)) previous.Dispose();
            capacityButton.Text = chinese ? "上下文结构" : "Context";
            projectsButton.Text = chinese ? "项目占比" : "Projects";
            conversationsButton.Text = chinese ? "对话明细" : "Conversations";
            capacityButton.AccessibleName = chinese ? "查看上下文容量结构" : "View context capacity structure";
            projectsButton.AccessibleName = chinese ? "查看每个项目占比" : "View usage by project";
            conversationsButton.AccessibleName = chinese ? "查看每个对话占用量" : "View usage by conversation";
            Font projectFont = projectsList.Font;
            projectsList.Font = PixelFont("Microsoft YaHei UI", 10.5f * uiScale, FontStyle.Regular);
            if (projectFont != null && !Object.ReferenceEquals(projectFont, Control.DefaultFont)) projectFont.Dispose();
            Font conversationFont = conversationsList.Font;
            conversationsList.Font = PixelFont("Microsoft YaHei UI", 10.5f * uiScale, FontStyle.Regular);
            if (conversationFont != null && !Object.ReferenceEquals(conversationFont, Control.DefaultFont)) conversationFont.Dispose();
            UpdateTabStyles();
            ConfigureColumns();
            UpdateLists();
        }

        private void UpdateTabStyles()
        {
            StyleTab(capacityButton, activeView == ContextBreakdownView.Capacity);
            StyleTab(projectsButton, activeView == ContextBreakdownView.Projects);
            StyleTab(conversationsButton, activeView == ContextBreakdownView.Conversations);
        }

        private void StyleTab(Button button, bool selected)
        {
            button.BackColor = selected ? Color.FromArgb(57, 122, 224) : Color.FromArgb(226, 235, 242);
            button.ForeColor = selected ? Color.White : Color.FromArgb(49, 61, 74);
            Font previous = button.Font;
            button.Font = PixelFont("Microsoft YaHei UI", 10.5f * uiScale, selected ? FontStyle.Bold : FontStyle.Regular);
            if (previous != null && !Object.ReferenceEquals(previous, Control.DefaultFont)) previous.Dispose();
        }

        private void ConfigureColumns()
        {
            projectsList.BeginUpdate();
            projectsList.Columns.Clear();
            projectsList.Columns.Add(chinese ? "项目" : "Project", Scale(130));
            projectsList.Columns.Add(chinese ? "项目路径" : "Project path", Scale(270));
            projectsList.Columns.Add(chinese ? "已占用" : "Occupied", Scale(90), HorizontalAlignment.Right);
            projectsList.Columns.Add(chinese ? "总容量" : "Capacity", Scale(90), HorizontalAlignment.Right);
            projectsList.Columns.Add(chinese ? "占用率" : "Usage", Scale(74), HorizontalAlignment.Right);
            projectsList.Columns.Add(chinese ? "总占比" : "Total share", Scale(80), HorizontalAlignment.Right);
            projectsList.Columns.Add(chinese ? "对话数" : "Chats", Scale(64), HorizontalAlignment.Right);
            projectsList.EndUpdate();

            conversationsList.BeginUpdate();
            conversationsList.Columns.Clear();
            conversationsList.Columns.Add(chinese ? "对话" : "Conversation", Scale(155));
            conversationsList.Columns.Add(chinese ? "项目" : "Project", Scale(115));
            conversationsList.Columns.Add(chinese ? "已占用" : "Occupied", Scale(86), HorizontalAlignment.Right);
            conversationsList.Columns.Add(chinese ? "容量" : "Capacity", Scale(86), HorizontalAlignment.Right);
            conversationsList.Columns.Add(chinese ? "占用率" : "Usage", Scale(72), HorizontalAlignment.Right);
            conversationsList.Columns.Add(chinese ? "总占比" : "Total share", Scale(74), HorizontalAlignment.Right);
            conversationsList.Columns.Add(chinese ? "项目内占比" : "Project share", Scale(84), HorizontalAlignment.Right);
            conversationsList.Columns.Add(chinese ? "最后活动" : "Updated", Scale(104));
            conversationsList.EndUpdate();
        }

        private void UpdateLists()
        {
            if (projectsList == null || conversationsList == null || history == null) return;
            long total = Math.Max(1, history.Context == null ? 0 : history.Context.InputTokens);
            System.Collections.Generic.Dictionary<string, long> projectTotals =
                new System.Collections.Generic.Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (ProjectTokenUsage project in history.Projects)
            {
                if (project.ContextConversations <= 0) continue;
                string key = String.IsNullOrWhiteSpace(project.ProjectPath) ? "(unknown)" : project.ProjectPath;
                projectTotals[key] = project.ContextInputTokens;
            }

            projectsList.BeginUpdate();
            projectsList.Items.Clear();
            foreach (ProjectTokenUsage project in history.Projects)
            {
                if (project.ContextConversations <= 0) continue;
                double share = project.ContextInputTokens * 100d / total;
                ListViewItem item = new ListViewItem(String.IsNullOrWhiteSpace(project.ProjectName) ? (chinese ? "未知项目" : "Unknown project") : project.ProjectName);
                item.SubItems.Add(String.IsNullOrWhiteSpace(project.ProjectPath) ? "—" : project.ProjectPath);
                item.SubItems.Add(FormatTokens(project.ContextInputTokens, chinese));
                item.SubItems.Add(FormatTokens(project.ContextCapacityTokens, chinese));
                double usage = project.ContextCapacityTokens <= 0 ? 0 : project.ContextInputTokens * 100d / project.ContextCapacityTokens;
                item.SubItems.Add(usage.ToString("0.0", CultureInfo.CurrentCulture) + "%");
                item.SubItems.Add(share.ToString("0.0", CultureInfo.CurrentCulture) + "%");
                item.SubItems.Add(project.ContextConversations.ToString(CultureInfo.CurrentCulture));
                item.ToolTipText = String.IsNullOrWhiteSpace(project.ProjectPath) ? project.ProjectName : project.ProjectPath;
                if (share >= 25) item.ForeColor = Color.FromArgb(182, 82, 58);
                else if (share >= 10) item.ForeColor = Color.FromArgb(176, 119, 31);
                projectsList.Items.Add(item);
            }
            projectsList.EndUpdate();

            conversationsList.BeginUpdate();
            conversationsList.Items.Clear();
            foreach (ConversationTokenUsage conversation in history.Conversations)
            {
                if (!conversation.ContextAvailable) continue;
                string key = String.IsNullOrWhiteSpace(conversation.ProjectPath) ? "(unknown)" : conversation.ProjectPath;
                long projectTotal;
                if (!projectTotals.TryGetValue(key, out projectTotal)) projectTotal = conversation.ContextInputTokens;
                double totalShare = conversation.ContextInputTokens * 100d / total;
                double projectShare = projectTotal <= 0 ? 0 : conversation.ContextInputTokens * 100d / projectTotal;
                string id = String.IsNullOrWhiteSpace(conversation.SessionId)
                    ? "unknown"
                    : conversation.SessionId.Substring(0, Math.Min(8, conversation.SessionId.Length));
                string label = conversation.StartedLocal.ToString(chinese ? "M-d HH:mm" : "MMM d HH:mm", CultureInfo.CurrentCulture) + " · " + id;
                ListViewItem item = new ListViewItem(label);
                item.SubItems.Add(String.IsNullOrWhiteSpace(conversation.ProjectName) ? (chinese ? "未知项目" : "Unknown") : conversation.ProjectName);
                item.SubItems.Add(FormatTokens(conversation.ContextInputTokens, chinese));
                item.SubItems.Add(FormatTokens(conversation.ContextCapacityTokens, chinese));
                double usage = conversation.ContextCapacityTokens <= 0 ? 0 : conversation.ContextInputTokens * 100d / conversation.ContextCapacityTokens;
                item.SubItems.Add(usage.ToString("0.0", CultureInfo.CurrentCulture) + "%");
                item.SubItems.Add(totalShare.ToString("0.0", CultureInfo.CurrentCulture) + "%");
                item.SubItems.Add(projectShare.ToString("0.0", CultureInfo.CurrentCulture) + "%");
                item.SubItems.Add(conversation.UpdatedLocal.ToString(chinese ? "M-d HH:mm" : "MMM d HH:mm", CultureInfo.CurrentCulture));
                if (totalShare >= 10) item.ForeColor = Color.FromArgb(182, 82, 58);
                else if (totalShare >= 5) item.ForeColor = Color.FromArgb(176, 119, 31);
                conversationsList.Items.Add(item);
            }
            conversationsList.EndUpdate();
        }

        private void LayoutControls()
        {
            if (backButton == null) return;
            backButton.Bounds = new Rectangle(Scale(28), Scale(22), Scale(78), Scale(34));
            int tabWidth = chinese ? 96 : 122;
            int gap = 6;
            int start = Math.Max(130, LogicalClientWidth - 28 - tabWidth * 3 - gap * 2);
            capacityButton.Bounds = new Rectangle(Scale(start), Scale(94), Scale(tabWidth), Scale(34));
            projectsButton.Bounds = new Rectangle(Scale(start + tabWidth + gap), Scale(94), Scale(tabWidth), Scale(34));
            conversationsButton.Bounds = new Rectangle(Scale(start + (tabWidth + gap) * 2), Scale(94), Scale(tabWidth), Scale(34));
            Rectangle listBounds = new Rectangle(Scale(52), Scale(158), Scale(Math.Max(600, LogicalClientWidth - 104)), Scale(Math.Max(330, LogicalClientHeight - 196)));
            projectsList.Bounds = listBounds;
            conversationsList.Bounds = listBounds;
            projectsList.Visible = activeView == ContextBreakdownView.Projects;
            conversationsList.Visible = activeView == ContextBreakdownView.Conversations;
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
                Color.FromArgb(229, 243, 251), Color.FromArgb(248, 249, 247), 18f))
                g.FillRectangle(wash, 0, 0, width, height);

            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            {
                g.DrawString(chinese ? "上下文总占用明细" : "Aggregate context occupancy details", titleFont, ink, 126, 22);
                string subtitle;
                if (activeView == ContextBreakdownView.Projects)
                    subtitle = chinese ? "按项目汇总每个对话最新上下文占用，并显示容量与总占比" : "Latest context occupancy grouped by project, with capacity and total share";
                else if (activeView == ContextBreakdownView.Conversations)
                    subtitle = chinese ? "逐个列出对话最新上下文占用、窗口容量与占用率" : "Latest context occupancy, window capacity, and usage for every conversation";
                else
                    subtitle = chinese ? "汇总所有对话最后一次上下文快照；历史 Token 吞吐不会计入"
                        : "Aggregates the final context snapshot of every conversation; Token throughput is excluded";
                g.DrawString(
                    subtitle,
                    subtitleFont, secondary, 126, 58);
            }

            Rectangle panel = new Rectangle(28, 140, Math.Max(620, width - 56), Math.Max(360, height - 166));
            using (GraphicsPath path = RoundedRect(panel, 20))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(248, 251, 252)))
                g.FillPath(fill, path);
            using (GraphicsPath edge = RoundedRect(new Rectangle(panel.X, panel.Y, panel.Width - 1, panel.Height - 1), 20))
            using (Pen stroke = new Pen(Color.FromArgb(48, 92, 119, 142), 1f))
                g.DrawPath(stroke, edge);

            if (activeView != ContextBreakdownView.Capacity) return;
            ContextCapacitySnapshot context = history == null ? null : history.Context;
            if (context == null || !context.Available || context.InputTokens <= 0)
            {
                using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
                    g.DrawString(chinese ? "本地对话中尚无可用的上下文容量快照" : "No context-capacity snapshot is available in local conversations",
                        sectionFont, secondary, panel.X + 40, panel.Y + panel.Height / 2 - 16);
                return;
            }

            long occupied = Math.Max(0, context.InputTokens);
            long capacity = Math.Max(occupied, context.CapacityTokens);
            long remaining = Math.Max(0, capacity - occupied);
            double usedPercent = capacity <= 0 ? 0 : Math.Min(100, occupied * 100d / capacity);
            double remainingPercent = Math.Max(0, 100 - usedPercent);
            Color occupiedColor = ContextStateColor(usedPercent);
            Color remainingColor = Color.FromArgb(214, 225, 232);

            DrawRing(g, new Rectangle(panel.X + 48, panel.Y + 78, 210, 210),
                usedPercent, 0, 0, remainingPercent,
                occupiedColor, Color.Transparent, Color.Transparent, remainingColor);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            {
                string amount = usedPercent.ToString("0", CultureInfo.CurrentCulture) + "%";
                SizeF amountSize = g.MeasureString(amount, percentFont);
                g.DrawString(amount, percentFont, ink, panel.X + 153 - amountSize.Width / 2, panel.Y + 144);
                string label = chinese ? "汇总占用率" : "aggregate usage";
                SizeF labelSize = g.MeasureString(label, labelFont);
                g.DrawString(label, labelFont, secondary, panel.X + 153 - labelSize.Width / 2, panel.Y + 184);
                string total = FormatTokens(occupied, chinese) + " / " + FormatTokens(capacity, chinese);
                SizeF totalSize = g.MeasureString(total, metaFont);
                g.DrawString(total, metaFont, secondary, panel.X + 153 - totalSize.Width / 2, panel.Y + 214);
            }

            int rightX = panel.X + 306;
            int rightWidth = panel.Right - rightX - 30;
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
                g.DrawString(chinese ? "上下文容量结构" : "Context capacity structure", sectionFont, ink, rightX, panel.Y + 34);
            DrawStackedBar(g, new Rectangle(rightX, panel.Y + 72, rightWidth, 14),
                usedPercent, 0, 0, remainingPercent,
                occupiedColor, Color.Transparent, Color.Transparent, remainingColor);
            DrawSegmentRow(g, rightX, panel.Y + 110, rightWidth, chinese ? "已占用上下文" : "Occupied context",
                occupied, usedPercent, occupiedColor,
                chinese ? "每个对话最新可见信息集合的汇总" : "sum of the latest visible information set in every conversation");
            DrawSegmentRow(g, rightX, panel.Y + 174, rightWidth, chinese ? "剩余容量" : "Remaining capacity",
                remaining, remainingPercent, remainingColor,
                chinese ? "所有可读取对话窗口的剩余空间汇总" : "remaining space across all readable conversation windows");
            DrawSegmentRow(g, rightX, panel.Y + 238, rightWidth, chinese ? "上下文总容量" : "Total context capacity",
                capacity, 100, Color.FromArgb(57, 122, 224),
                chinese ? "各对话最新 model_context_window 汇总" : "sum of each conversation's latest model_context_window");

            int footerY = panel.Bottom - 92;
            Rectangle footer = new Rectangle(rightX, footerY, rightWidth, 68);
            using (GraphicsPath footerPath = RoundedRect(footer, 12))
            using (SolidBrush footerFill = new SolidBrush(Color.FromArgb(238, 244, 248)))
                g.FillPath(footerFill, footerPath);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(35, 46, 58)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            {
                g.DrawString(chinese ? "口径说明" : "Metric definition", labelFont, ink, footer.X + 16, footer.Y + 10);
                string values = chinese
                    ? "逐对话最新快照 · 排除历史 total_token_usage"
                        + ContextSampleSuffix(context, true)
                    : "Latest snapshot per conversation · historical total_token_usage excluded"
                        + ContextSampleSuffix(context, false);
                g.DrawString(values, metaFont, secondary, new RectangleF(footer.X + 16, footer.Y + 37, footer.Width - 32, 18));
            }
        }

        private void DrawRing(Graphics g, Rectangle bounds, double cached, double fresh, double unknown, double remaining,
            Color cachedColor, Color freshColor, Color unknownColor, Color remainingColor)
        {
            Rectangle ring = new Rectangle(bounds.X + 18, bounds.Y + 18, bounds.Width - 36, bounds.Height - 36);
            using (Pen track = new Pen(Color.FromArgb(225, 232, 237), 24f))
                g.DrawArc(track, ring, -90, 360);
            float start = -90f;
            DrawRingSegment(g, ring, ref start, cached, cachedColor);
            DrawRingSegment(g, ring, ref start, fresh, freshColor);
            DrawRingSegment(g, ring, ref start, unknown, unknownColor);
            DrawRingSegment(g, ring, ref start, remaining, remainingColor);
        }

        private static void DrawRingSegment(Graphics g, Rectangle ring, ref float start, double percent, Color color)
        {
            float sweep = (float)Math.Max(0, Math.Min(360, percent * 3.6));
            if (sweep <= 0) return;
            using (Pen pen = new Pen(color, 24f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                g.DrawArc(pen, ring, start, sweep);
            start += sweep;
        }

        private void DrawStackedBar(Graphics g, Rectangle bounds, double cached, double fresh, double unknown, double remaining,
            Color cachedColor, Color freshColor, Color unknownColor, Color remainingColor)
        {
            using (GraphicsPath track = RoundedRect(bounds, 7))
            using (SolidBrush trackFill = new SolidBrush(Color.FromArgb(226, 233, 238)))
                g.FillPath(trackFill, track);
            int cachedWidth = (int)Math.Round(bounds.Width * cached / 100d);
            int freshWidth = (int)Math.Round(bounds.Width * fresh / 100d);
            int unknownWidth = (int)Math.Round(bounds.Width * unknown / 100d);
            int remainingWidth = Math.Max(0, bounds.Width - cachedWidth - freshWidth - unknownWidth);
            if (cachedWidth > 0)
                using (SolidBrush fill = new SolidBrush(cachedColor)) g.FillRectangle(fill, bounds.X, bounds.Y, cachedWidth, bounds.Height);
            if (freshWidth > 0)
                using (SolidBrush fill = new SolidBrush(freshColor)) g.FillRectangle(fill, bounds.X + cachedWidth, bounds.Y, freshWidth, bounds.Height);
            if (unknownWidth > 0)
                using (SolidBrush fill = new SolidBrush(unknownColor)) g.FillRectangle(fill, bounds.X + cachedWidth + freshWidth, bounds.Y, unknownWidth, bounds.Height);
            if (remainingWidth > 0)
                using (SolidBrush fill = new SolidBrush(remainingColor)) g.FillRectangle(fill, bounds.X + cachedWidth + freshWidth + unknownWidth, bounds.Y, remainingWidth, bounds.Height);
        }

        private void DrawSegmentRow(Graphics g, int x, int y, int width, string label, long value, double percent, Color color, string note)
        {
            string amount = FormatTokens(value, chinese) + " · " + percent.ToString("0.0", CultureInfo.CurrentCulture) + "%";
            DrawSegmentTextRow(g, x, y, width, label, amount, color, note);
        }

        private void DrawSegmentTextRow(Graphics g, int x, int y, int width, string label, string amount, Color color, string note)
        {
            using (SolidBrush strip = new SolidBrush(color))
                g.FillRectangle(strip, x, y, 4, 42);
            using (SolidBrush ink = new SolidBrush(Color.FromArgb(23, 29, 37)))
            using (SolidBrush secondary = new SolidBrush(Color.FromArgb(75, 87, 101)))
            {
                g.DrawString(label, labelFont, ink, x + 16, y);
                SizeF amountSize = g.MeasureString(amount, valueFont);
                g.DrawString(amount, valueFont, ink, x + width - amountSize.Width, y - 4);
                g.DrawString(note, metaFont, secondary, x + 16, y + 25);
            }
        }

        private static string ContextSampleSuffix(ContextCapacitySnapshot context, bool chinese)
        {
            if (context == null || context.SampleUtc == DateTime.MinValue) return String.Empty;
            string time = context.SampleUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            return chinese ? " · 更新 " + time : " · updated " + time;
        }

        private string Guidance(double remainingPercent)
        {
            if (remainingPercent >= 50) return chinese ? "健康，暂无需整理" : "healthy, no cleanup needed";
            if (remainingPercent >= 10) return chinese ? "谨慎，建议整理无关上下文" : "caution, trim unrelated context";
            return chinese ? "紧急，建议总结后新建任务" : "critical, summarize and start a new task";
        }

        private static Color RemainingColor(double remainingPercent)
        {
            if (remainingPercent >= 50) return Color.FromArgb(51, 200, 120);
            if (remainingPercent >= 10) return Color.FromArgb(214, 155, 45);
            return Color.FromArgb(233, 93, 79);
        }

        private static Color ContextStateColor(double usedPercent)
        {
            if (usedPercent <= 50) return Color.FromArgb(51, 200, 120);
            if (usedPercent <= 90) return Color.FromArgb(214, 155, 45);
            return Color.FromArgb(233, 93, 79);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                Close();
                e.Handled = true;
            }
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
            UpdateLanguage();
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

        private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                titleFont.Dispose();
                subtitleFont.Dispose();
                sectionFont.Dispose();
                percentFont.Dispose();
                valueFont.Dispose();
                labelFont.Dispose();
                metaFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
