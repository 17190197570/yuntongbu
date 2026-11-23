using CloudDrive.Storage;
using System.Drawing.Drawing2D;

namespace CloudDrive.Desktop;

public sealed partial class MainForm
{
    private static readonly Color Accent = Color.FromArgb(22, 119, 255);
    private readonly List<Button> connectionCancel = [];
    private static Control ConnectionHeading(string title, string description)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        panel.Controls.Add(new Label { Text = description, Dock = DockStyle.Bottom, Height = 42, ForeColor = Muted });
        panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 28, Font = new("Microsoft YaHei UI", 11, FontStyle.Bold), ForeColor = Ink });
        return panel;
    }
    private static ConnectionCard FormCard(string title, Control content)
    {
        var card = new ConnectionCard { Dock = DockStyle.Fill, Margin = new(0, 0, 0, 12) };
        var heading = new Label { Text = title, Dock = DockStyle.Top, Height = 32, Font = StrongFont, ForeColor = Ink };
        content.Dock = DockStyle.Fill; card.Controls.Add(content); card.Controls.Add(heading); return card;
    }
    private static void ConfigureConnectionList(ListBox list)
    {
        list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 66; list.BorderStyle = BorderStyle.None; list.HorizontalScrollbar = false; list.IntegralHeight = false; list.BackColor = Color.White;
        list.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var background = new SolidBrush(selected ? Color.FromArgb(230, 244, 255) : Color.White); e.Graphics.FillRectangle(background, e.Bounds);
            if (selected) { using var stripe = new SolidBrush(Accent); e.Graphics.FillRectangle(stripe, e.Bounds.Left, e.Bounds.Top + 8, 3, e.Bounds.Height - 16); }
            object item = list.Items[e.Index];
            string title = item is StorageConnection s ? s.Server + ":" + s.Port : item is SmbConnection c ? Path.GetFileName(Path.TrimEndingDirectorySeparator(c.SyncRoot)) : item.ToString() ?? "";
            string detail = item is StorageConnection nas ? nas.Share + " · " + nas.Account : item is SmbConnection mapping ? mapping.Server + " / " + mapping.Share + (mapping.SubPath.Length > 0 ? " / " + mapping.SubPath : "") : "";
            var flags = TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            TextRenderer.DrawText(e.Graphics, title, e.Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 8, e.Bounds.Width - 22, 24), selected ? Accent : Color.FromArgb(31, 31, 31), flags);
            TextRenderer.DrawText(e.Graphics, detail, e.Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y + 34, e.Bounds.Width - 22, 22), Color.FromArgb(102, 102, 102), flags);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        };
    }
    private static Control ConnectionSidebar(string title, ListBox list, Button add)
    {
        ConfigureConnectionList(list);
        var card = new ConnectionCard { Dock = DockStyle.Fill, Padding = new(12) };
        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new(SizeType.Absolute, 46)); table.RowStyles.Add(new(SizeType.Percent, 100)); table.RowStyles.Add(new(SizeType.Absolute, 50));
        table.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, Font = new("Microsoft YaHei UI", 11, FontStyle.Bold), Padding = new(8, 8, 0, 0) }, 0, 0);
        table.Controls.Add(list, 0, 1); add.Dock = DockStyle.Fill; add.Margin = new(3, 10, 3, 0); table.Controls.Add(add, 0, 2); card.Controls.Add(table); return card;
    }
    private static Button MainAction(string text, EventHandler handler) { var b = Button(text, handler); b.Tag = "primary"; return b; }
    private static Button DestructiveAction(string text, EventHandler handler) { var b = Button(text, handler); b.Tag = "danger"; return b; }
    private Button CancelConnectionAction()
    {
        var b = Button("取消操作", (_, _) => operation?.Cancel()); b.Enabled = false; connectionCancel.Add(b); return b;
    }
    private void SetConnectionBusy(bool busy)
    {
        foreach (Control control in inputs.Values.Cast<Control>().Concat([port, storages, connections, mappingStorage, localFolder, remoteFolder, createFolders, auto])) control.Enabled = !busy;
        foreach (var button in connectionCancel) button.Enabled = busy;
    }
    private sealed class ConnectionCard : Panel
    {
        public ConnectionCard() { DoubleBuffered = true; Padding = new(20); BackColor = Color.White; SetStyle(ControlStyles.ResizeRedraw, true); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundRect(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 8);
            using var fill = new SolidBrush(Color.White); e.Graphics.FillPath(fill, path);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (Width < 16 || Height < 16) return;
            using var outline = RoundRect(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 8);
            using var border = new Pen(Outline); e.Graphics.DrawPath(border, outline);
        }
    }
}
