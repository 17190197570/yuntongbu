using CloudDrive.Storage;
using System.Drawing.Drawing2D;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    // Ant Design's light surface hierarchy, spacing and semantic colors, rendered natively.
    private static readonly Color Canvas = Color.FromArgb(245, 247, 250);
    private static readonly Color Ink = Color.FromArgb(31, 38, 51);
    private static readonly Color Muted = Color.FromArgb(107, 116, 133);
    private static readonly Color Outline = Color.FromArgb(229, 233, 240);
    private static readonly Color BlueWash = Color.FromArgb(230, 244, 255);
    private static readonly Font BodyFont = new("Microsoft YaHei UI", 9F);
    private static readonly Font StrongFont = new("Microsoft YaHei UI", 9F, FontStyle.Bold);
    private static readonly Font TitleFont = new("Microsoft YaHei UI", 18F, FontStyle.Bold);
    private readonly Dictionary<TabPage, NavigationButton> navigation = [];
    private readonly Label pageTitle = new() { Dock = DockStyle.Top, Height = 38, Font = TitleFont };
    private readonly Label pageDescription = new() { Dock = DockStyle.Fill, ForeColor = Muted };
    private readonly Label runtimeBadge = new() { AutoSize = false, Width = 166, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Text = "●  后台待连接", ForeColor = Muted, BackColor = Canvas };
    private readonly Label[] homeValues = Enumerable.Range(0, 4).Select(_ => new Label { Dock = DockStyle.Fill, Font = new("Microsoft YaHei UI", 23, FontStyle.Bold), Text = "—", ForeColor = Ink }).ToArray();
    private TabControl? workspace;

    private void BuildAppShell(TabControl deck)
    {
        workspace = deck;
        BackColor = Canvas;
        var sidebar = new Panel { Dock = DockStyle.Left, Width = 192, BackColor = Color.White, Padding = new(16, 24, 16, 16) };
        sidebar.Paint += (_, e) => { using var pen = new Pen(Outline); e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height); };
        var brand = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.White };
        var mark = new Label { Text = "C", ForeColor = Color.White, BackColor = Accent, Font = new("Segoe UI", 21, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Bounds = new(2, 0, 40, 40) };
        brand.Controls.Add(mark);
        brand.Controls.Add(new Label { Text = "CloudDrive", Font = new("Segoe UI", 12, FontStyle.Bold), ForeColor = Ink, Bounds = new(52, 1, 110, 25) });
        brand.Controls.Add(new Label { Text = "LITE  /  云端文件空间", Font = new("Microsoft YaHei UI", 8), ForeColor = Muted, Bounds = new(52, 29, 120, 22) });
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.White, Padding = Padding.Empty };
        nav.Layout += (_, _) =>
        {
            int width = Math.Max(112, nav.ClientSize.Width - 4);
            foreach (Control child in nav.Controls) if (child.Width != width) child.Width = width;
        };
        string[] groups = ["工作空间", "", "", "", "文件管理", "", "", "系统与维护", "", ""];
        string[] names = ["首页总览", "NAS 连接", "文件夹映射", "存储状态", "同步任务", "缓存管理", "归档管理", "任务日志", "运行健康", "设置与运维"];
        string[] descriptions = ["查看连接、任务与文件空间的当前状态。", "管理 NAS 地址、共享与登录凭据。", "让本地文件夹对应到 NAS 上的远端目录。", "查看各个映射的连接状态与索引进度。", "查看新增文件试验任务与处理进度。", "查看本地缓存、保留范围与清理策略。", "管理跨存储迁移计划与执行记录。", "查看扫描、连接、文件读取与重试记录。", "检查运行状态，查看诊断依据与恢复记录。", "管理启动、备份、日志与客户端设置。"];
        for (int i = 0; i < deck.TabPages.Count; i++)
        {
            var page = deck.TabPages[i];
            if (groups[i].Length > 0) nav.Controls.Add(new Label { Text = groups[i], ForeColor = Muted, AutoSize = false, Width = 154, Height = 30, Padding = new(12, 9, 0, 0), Font = new("Microsoft YaHei UI", 8), Margin = new(0, i == 0 ? 0 : 16, 0, 6) });
            var button = new NavigationButton(i) { Text = names[i], AutoSize = false, Width = 158, Height = 42, Margin = new(0, 0, 0, 4), AccessibleName = names[i] };
            button.Click += (_, _) => deck.SelectedTab = page;
            navigation.Add(page, button); nav.Controls.Add(button);
            page.Padding = Padding.Empty; page.BackColor = Canvas;
            if (i is >= 3 and <= 8) WrapPageSurface(page);
        }
        var footer = new Label { Dock = DockStyle.Bottom, Height = 50, Text = "开发版  " + ProductEnvironment.Version + "\nWindows 原生按需文件", ForeColor = Muted, Padding = new(10, 8, 0, 0), Font = new("Microsoft YaHei UI", 8) };
        sidebar.Controls.Add(nav); sidebar.Controls.Add(footer); sidebar.Controls.Add(brand);
        var content = new Panel { Dock = DockStyle.Fill, Padding = new(24, 0, 24, 20), BackColor = Canvas };
        var top = new Panel { Dock = DockStyle.Top, Height = 45, Padding = new(0, 8, 0, 0) };
        top.Controls.Add(new Label { Text = "工作空间  /  CloudDrive Lite", Dock = DockStyle.Left, Width = 300, ForeColor = Muted, Padding = new(0, 8, 0, 0), Font = new("Microsoft YaHei UI", 8) });
        runtimeBadge.Dock = DockStyle.Right; top.Controls.Add(runtimeBadge);
        var heading = new Panel { Dock = DockStyle.Top, Height = 88, Padding = new(0, 5, 0, 8) };
        heading.Controls.Add(pageDescription); heading.Controls.Add(pageTitle);
        content.Controls.Add(deck); content.Controls.Add(heading); content.Controls.Add(top);
        Controls.Add(content); Controls.Add(sidebar);
        void SelectPage()
        {
            int selected = Math.Max(deck.SelectedIndex, 0); pageTitle.Text = names[selected]; pageDescription.Text = descriptions[selected];
            foreach (var pair in navigation) { pair.Value.Selected = pair.Key == deck.SelectedTab; pair.Value.Invalidate(); }
        }
        deck.SelectedIndexChanged += (_, _) => SelectPage(); SelectPage();
    }

    private static void WrapPageSurface(TabPage page)
    {
        var children = page.Controls.Cast<Control>().ToArray(); page.Controls.Clear();
        var card = new ConnectionCard { Dock = DockStyle.Fill, Padding = new(16) };
        card.Controls.AddRange(children); page.Controls.Add(card);
    }

    private void NavigateWorkspace(int page) { if (workspace is not null) workspace.SelectedIndex = page; }
    private void UpdateHomeProfiles()
    {
        homeValues[0].Text = index.Storages().Count.ToString("N0"); homeValues[1].Text = index.Connections().Count.ToString("N0");
    }
    private void UpdateHomeMetrics(long indexed, long bytes)
    {
        UpdateHomeProfiles();
        homeValues[2].Text = indexed.ToString("N0"); homeValues[3].Text = (bytes / (double)(1L << 30)).ToString("N2") + " GiB";
    }

    private async Task CaptureDesignSmokeAsync(TabControl deck)
    {
        var originalSize = Size;
        string folder = Path.Combine(AppContext.BaseDirectory, "design-smoke"); Directory.CreateDirectory(folder);
        try
        {
            foreach (var view in new[] { (Name: "normal", Size: originalSize), (Name: "minimum", Size: MinimumSize) })
            {
                Size = view.Size;
                for (int i = 0; i < deck.TabPages.Count; i++)
                {
                    var page = deck.TabPages[i]; var button = navigation[page];
                    ((ScrollableControl)button.Parent!).ScrollControlIntoView(button); button.Focus(); button.PerformClick(); await Task.Delay(60);
                    if (deck.SelectedTab != page || navigation.Count(p => p.Value.Selected) != 1 || !navigation[page].Selected || pageTitle.Text != navigation[page].Text)
                        throw new IOException("侧边导航、选中状态或页面标题不一致。");
                    if (!button.Visible || !button.Parent!.ClientRectangle.Contains(button.Bounds))
                        throw new IOException($"导航滚动后仍被裁切：page={i}, window={Size}, dpi={DeviceDpi}, bounds={button.Bounds}, viewport={button.Parent!.ClientRectangle}。");
                    using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                    bitmap.Save(Path.Combine(folder, $"{view.Name}-{i:D2}.png"));
                }
                foreach (var input in inputs.Values.Cast<Control>().Concat([port, localFolder, remoteFolder, mappingStorage]))
                    if (input.Width < 90 || input.Height < 16 || input.Parent is null || input.Bottom > input.Parent.ClientSize.Height)
                        throw new IOException("窗口尺寸导致连接或映射输入控件裁切。");
            }
            var menu = (FlowLayoutPanel)navigation.First().Value.Parent!; var maximum = menu.MaximumSize;
            try
            {
                menu.MaximumSize = new(0, 300);
                foreach (var pair in navigation)
                {
                    menu.ScrollControlIntoView(pair.Value); pair.Value.Focus(); pair.Value.PerformClick(); await Task.Delay(30);
                    if (deck.SelectedTab != pair.Key || !menu.ClientRectangle.Contains(pair.Value.Bounds)) throw new IOException("紧凑侧栏滚动导航无法完整访问页面。");
                }
            }
            finally { menu.MaximumSize = maximum; }
            deck.SelectedIndex = 5; Size = originalSize;
            for (int i = 0; i < cachePages.TabPages.Count; i++)
            {
                cachePages.SelectedIndex = i; await Task.Delay(60);
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height)); bitmap.Save(Path.Combine(folder, $"cache-{i}.png"));
            }
            foreach (var popup in new[] { (Name: "product-settings", Form: CreateProductSettingsDialog()), (Name: "health-settings", Form: CreateHealthSettingsDialog()) })
            {
                using var dialog = popup.Form; dialog.Show(this); await Task.Delay(80);
                using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height)); bitmap.Save(Path.Combine(folder, popup.Name + ".png")); dialog.Close();
            }
        }
        finally { Size = originalSize; deck.SelectedIndex = 0; }
        File.WriteAllText(Path.Combine(folder, "result.txt"), $"PASS 10 real sidebar button navigations at normal and minimum sizes; selected state/title consistency; scroll-to-visible navigation including compact 300px sidebar; input geometry; all cache subpages. DeviceDpi={DeviceDpi}. Isolated fixture database, no user NAS accessed.");
    }

    private static void ApplyProductTheme(Control parent)
    {
        if (parent is Form dialog) { dialog.BackColor = Canvas; dialog.Font = BodyFont; dialog.Padding = dialog is MainForm ? Padding.Empty : new(16); }
        foreach (Control child in parent.Controls)
        {
            if (child is NavigationButton) continue;
            switch (child)
            {
                case ModernButton button:
                    button.Invalidate(); break;
                case Button button:
                    button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderColor = Outline; button.BackColor = Color.White; button.Cursor = Cursors.Hand; break;
                case DataGridView grid:
                    StyleGrid(grid); break;
                case TextBox box:
                    box.BorderStyle = box.Parent is InputSurface or NumericUpDown || box.Multiline && box.ReadOnly ? BorderStyle.None : BorderStyle.FixedSingle;
                    box.BackColor = box.ReadOnly ? Color.FromArgb(249, 250, 252) : Color.White;
                    if (box.Multiline) { box.Font = new("Microsoft YaHei UI", 9); box.Margin = new(8); }
                    break;
                case ListBox list:
                    list.BorderStyle = BorderStyle.None; list.BackColor = Color.White; list.IntegralHeight = false;
                    if (list.DrawMode == DrawMode.Normal) StyleActivityList(list);
                    break;
                case ComboBox combo: combo.FlatStyle = FlatStyle.Flat; combo.BackColor = Color.White; break;
                case NumericUpDown number: number.BorderStyle = number.Parent is InputSurface ? BorderStyle.None : BorderStyle.FixedSingle; number.BackColor = Color.White; break;
                case CheckBox flag: flag.Cursor = Cursors.Hand; break;
                case Label label:
                    if (label.Font.Size <= BodyFont.Size + 1 && !label.Font.Bold && label.ForeColor == SystemColors.ControlText) label.ForeColor = Muted;
                    break;
                case TabControl tabs when tabs is not PageDeck:
                    tabs.BackColor = Color.White;
                    tabs.DrawMode = TabDrawMode.OwnerDrawFixed; tabs.ItemSize = new(144, 38); tabs.SizeMode = TabSizeMode.Fixed;
                    tabs.DrawItem += (_, e) =>
                    {
                        bool active = e.Index == tabs.SelectedIndex; var rect = tabs.GetTabRect(e.Index);
                        using var fill = new SolidBrush(Color.White); e.Graphics.FillRectangle(fill, rect);
                        TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, BodyFont, rect, active ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                        if (active) { using var line = new Pen(Accent, 2); e.Graphics.DrawLine(line, rect.Left + 14, rect.Bottom - 2, rect.Right - 14, rect.Bottom - 2); }
                    };
                    break;
            }
            if (child is TabPage tab && tab.Parent is not PageDeck) { tab.UseVisualStyleBackColor = false; tab.BackColor = Color.White; }
            if (child is Panel && child.BackColor == SystemColors.Control) child.BackColor = parent.BackColor;
            if (child.HasChildren) ApplyProductTheme(child);
        }
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false; grid.BackgroundColor = Color.White; grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; grid.GridColor = Color.FromArgb(240, 242, 246);
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(248, 250, 252), ForeColor = Muted, Font = StrongFont, Padding = new(10, 0, 0, 0), SelectionBackColor = Color.FromArgb(248, 250, 252), SelectionForeColor = Ink };
        grid.DefaultCellStyle = new() { BackColor = Color.White, ForeColor = Ink, Font = BodyFont, SelectionBackColor = BlueWash, SelectionForeColor = Color.FromArgb(9, 88, 217), Padding = new(10, 4, 4, 4), NullValue = "—" };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.White; grid.RowTemplate.Height = 38; grid.AllowUserToResizeRows = false;
        foreach (DataGridViewColumn column in grid.Columns) column.MinimumWidth = 65;
        grid.Paint += (_, e) =>
        {
            if (grid.Rows.Count > 0 || grid.Height < 120) return;
            var rect = new Rectangle(12, grid.ColumnHeadersHeight + 12, grid.Width - 24, Math.Max(30, grid.Height - grid.ColumnHeadersHeight - 24));
            TextRenderer.DrawText(e.Graphics, "暂无记录\n完成配置或运行任务后，结果会显示在这里", BodyFont, rect, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        };
    }

    private static void StyleActivityList(ListBox list)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 38;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var fill = new SolidBrush(selected ? BlueWash : Color.White); e.Graphics.FillRectangle(fill, e.Bounds);
            using var line = new Pen(Outline); e.Graphics.DrawLine(line, e.Bounds.Left + 12, e.Bounds.Bottom - 1, e.Bounds.Right - 12, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), BodyFont, Rectangle.Inflate(e.Bounds, -12, 0), selected ? Accent : Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }

    private static GraphicsPath RoundRect(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (d <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90); path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90);
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90); path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
    }

    private sealed class PageDeck : TabControl
    {
        public PageDeck() { Appearance = TabAppearance.FlatButtons; SizeMode = TabSizeMode.Fixed; ItemSize = new(0, 1); Padding = Point.Empty; Dock = DockStyle.Fill; TabStop = false; }
        protected override void WndProc(ref Message m)
        {
            // TCM_ADJUSTRECT: the page deck has sidebar navigation, so no native tab header.
            if (m.Msg == 0x1328 && !DesignMode) { m.Result = (IntPtr)1; return; }
            base.WndProc(ref m);
        }
    }

    private sealed class InputSurface : Panel
    {
        public InputSurface(Control input)
        {
            Height = 36; MinimumSize = new(60, 36); Padding = new(10, 7, 8, 5); BackColor = Color.White; Margin = new(0, 0, 0, 8); TabStop = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            if (input is TextBox box) { box.BorderStyle = BorderStyle.None; box.AutoSize = false; }
            if (input is NumericUpDown number) number.BorderStyle = BorderStyle.None;
            input.Dock = DockStyle.Fill; input.Margin = Padding.Empty; input.TabIndex = 0; Controls.Add(input);
            input.GotFocus += (_, _) => Invalidate(); input.LostFocus += (_, _) => Invalidate(); input.EnabledChanged += (_, _) => Invalidate();
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Color.White); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new RectangleF(1, 1, Width - 3, Height - 3), 6); using var fill = new SolidBrush(Color.White); e.Graphics.FillPath(fill, path);
            using var border = new Pen(ContainsFocus ? Accent : Outline, ContainsFocus ? 2 : 1); e.Graphics.DrawPath(border, path);
        }
    }

    private sealed class SectionTabs : UserControl
    {
        private readonly PageDeck deck = new();
        private readonly FlowLayoutPanel header = new() { Dock = DockStyle.Top, Height = 44, BackColor = Color.White, Margin = Padding.Empty, WrapContents = false };
        private readonly Dictionary<TabPage, SectionButton> buttons = [];
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public TabControl.TabPageCollection TabPages => deck.TabPages;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int SelectedIndex { get => deck.SelectedIndex; set => deck.SelectedIndex = value; }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public TabPage? SelectedTab { get => deck.SelectedTab; set => deck.SelectedTab = value; }
        public SectionTabs()
        {
            Dock = DockStyle.Fill; BackColor = Color.White; TabStop = false;
            Controls.Add(deck); Controls.Add(header);
            deck.ControlAdded += (_, e) =>
            {
                if (e.Control is not TabPage page) return;
                page.UseVisualStyleBackColor = false; page.BackColor = Color.White;
                var button = new SectionButton { Text = page.Text, Margin = new(0, 0, 12, 0), Height = 40 };
                button.Click += (_, _) => deck.SelectedTab = page; buttons.Add(page, button); header.Controls.Add(button); UpdateSelected();
            };
            deck.SelectedIndexChanged += (_, _) => UpdateSelected();
            header.Paint += (_, e) => { using var pen = new Pen(Outline); e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1); };
        }
        private void UpdateSelected() { foreach (var pair in buttons) { pair.Value.Active = pair.Key == deck.SelectedTab; pair.Value.Invalidate(); } }
    }

    private sealed class SectionButton : ModernButton
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Active { get; set; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White);
            TextRenderer.DrawText(e.Graphics, Text, BodyFont, ClientRectangle, Active ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (Active) { using var pen = new Pen(Accent, 2); e.Graphics.DrawLine(pen, 10, Height - 2, Width - 10, Height - 2); }
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }

    private class ModernButton : Button
    {
        private bool hovered;
        public ModernButton() { FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; AutoSize = true; Height = 36; Padding = new(14, 6, 14, 6); Margin = new(0, 0, 8, 8); Cursor = Cursors.Hand; BackColor = Color.White; SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        public override Size GetPreferredSize(Size proposedSize) { var size = TextRenderer.MeasureText(Text, Font); return new(Math.Max(76, size.Width + Padding.Horizontal + 6), Math.Max(36, size.Height + Padding.Vertical + 4)); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool primary = Tag as string == "primary", danger = Tag as string == "danger";
            var fill = !Enabled ? Color.FromArgb(245, 246, 248) : primary ? hovered ? Color.FromArgb(64, 150, 255) : Accent : hovered ? BlueWash : Color.White;
            var ink = !Enabled ? Color.FromArgb(160, 167, 179) : primary ? Color.White : danger ? Color.FromArgb(207, 19, 34) : hovered ? Accent : Ink;
            using var path = RoundRect(new RectangleF(1, 1, Width - 3, Height - 3), 6);
            using var brush = new SolidBrush(fill); e.Graphics.FillPath(brush, path);
            using var pen = new Pen(Focused || hovered ? Accent : danger ? Color.FromArgb(255, 204, 199) : primary ? Accent : Outline, Focused ? 2 : 1); e.Graphics.DrawPath(pen, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (ShowFocusCues && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), ink, fill);
        }
    }

    private sealed class NavigationButton(int icon) : ModernButton
    {
        private bool over;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }
        protected override void OnMouseEnter(EventArgs e) { over = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; base.OnMouseLeave(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.White); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Selected || over) { using var shape = RoundRect(new RectangleF(0, 0, Width - 1, Height - 1), 8); using var fill = new SolidBrush(Selected ? BlueWash : Canvas); e.Graphics.FillPath(fill, shape); }
            Color color = Selected ? Accent : Muted;
            using var pen = new Pen(color, 1.6F) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            int x = 14, y = Height / 2 - 8;
            if (icon is 1 or 3) { using var path = RoundRect(new RectangleF(x, y, 18, 16), 3); e.Graphics.DrawPath(pen, path); e.Graphics.DrawLine(pen, x + 4, y + 6, x + 14, y + 6); e.Graphics.DrawEllipse(pen, x + 13, y + 11, 1, 1); }
            else if (icon is 2 or 6) { e.Graphics.DrawLines(pen, [new(x, y + 4), new(x + 6, y + 4), new(x + 8, y + 1), new(x + 17, y + 1), new(x + 17, y + 16), new(x, y + 16), new(x, y + 4)]); }
            else if (icon is 4 or 5) { e.Graphics.DrawArc(pen, x, y, 17, 17, 30, 260); e.Graphics.DrawLines(pen, [new(x + 12, y), new(x + 17, y + 1), new(x + 17, y + 6)]); }
            else if (icon == 8) { e.Graphics.DrawLines(pen, [new(x, y + 9), new(x + 4, y + 9), new(x + 7, y + 2), new(x + 11, y + 16), new(x + 14, y + 9), new(x + 18, y + 9)]); }
            else if (icon == 9) { e.Graphics.DrawEllipse(pen, x, y, 17, 17); e.Graphics.DrawEllipse(pen, x + 6, y + 6, 5, 5); }
            else { e.Graphics.DrawRectangle(pen, x, y, 17, 17); e.Graphics.DrawLine(pen, x + 6, y, x + 6, y + 17); e.Graphics.DrawLine(pen, x + 6, y + 6, x + 17, y + 6); }
            TextRenderer.DrawText(e.Graphics, Text, BodyFont, new Rectangle(44, 0, Width - 48, Height), Selected ? Accent : Ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
