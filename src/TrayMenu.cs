using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;

internal sealed class TrayMenu : ContextMenuStrip
{
    private const int TextInset = 42;
    private const int CheckInset = 20;

    private readonly ToolStripSystemRenderer systemRenderer = new ToolStripSystemRenderer();
    private readonly TrayMenuRenderer trayRenderer;
    private readonly Func<AppearanceMode> appearance;
    private readonly Func<string> tooltip;
    private SettingsTheme theme;
    private bool positioning;

    internal TrayMenu(Func<AppearanceMode> appearance, Func<string> tooltip)
    {
        this.appearance = appearance;
        this.tooltip = tooltip;
        AccessibleName = Program.Name;
        ShowImageMargin = false;
        trayRenderer = new TrayMenuRenderer(this);
    }

    internal ToolStripMenuItem AddAction(string text, TrayMenuIcon symbol, EventHandler action)
    {
        var item = new ToolStripMenuItem(text, null, action) { Tag = symbol };
        Items.Add(item);
        return item;
    }

    protected override void SetVisibleCore(bool visible)
    {
        if (!visible || Visible || positioning)
        {
            base.SetVisibleCore(visible);
            return;
        }

        ApplyAppearance(appearance());
        Point cursor = Cursor.Position;
        Rectangle workArea = Screen.FromPoint(cursor).WorkingArea;
        Rectangle bounds = GetTrayIconBounds(cursor, tooltip());
        Size size = GetPreferredSize(Size.Empty);
        int x = bounds.IsEmpty ? cursor.X : bounds.Left + bounds.Width / 2;
        int above = (bounds.IsEmpty ? Math.Min(cursor.Y, workArea.Bottom) : bounds.Top) - size.Height;
        int below = bounds.IsEmpty ? cursor.Y : bounds.Bottom;
        // Show at the measured position while NotifyIcon is handling menu activation.
        positioning = true;
        try { Show(new Point(x - size.Width / 2, above >= workArea.Top ? above : below)); }
        finally { positioning = false; }
    }

    private Rectangle GetTrayIconBounds(Point cursor, string tooltip)
    {
        Point physicalCursor;
        if (!GetPhysicalCursorPos(out physicalCursor)) return Rectangle.Empty;
        IntPtr trayWindow = WindowFromPhysicalPoint(physicalCursor);
        uint shellProcess, hitProcess;
        WindowsInput.GetWindowThreadProcessId(GetShellWindow(), out shellProcess);
        WindowsInput.GetWindowThreadProcessId(trayWindow, out hitProcess);
        if (hitProcess == 0 || hitProcess != shellProcess) return Rectangle.Empty;

        try
        {
            var hit = AutomationElement.FromPoint(new System.Windows.Point(physicalCursor.X, physicalCursor.Y));
            if (hit == null) return Rectangle.Empty;
            // Explorer can return the overflow flyout itself for a hit inside an icon.
            var icon = hit.FindFirst(TreeScope.Subtree,
                new PropertyCondition(AutomationElement.NameProperty, tooltip));
            if (icon == null || icon.Current.IsOffscreen) return Rectangle.Empty;
            var bounds = icon.Current.BoundingRectangle;
            uint trayDpi = GetDpiForWindow(trayWindow);
            if (bounds.IsEmpty || trayDpi == 0) return Rectangle.Empty;

            Location = cursor;
            // UI Automation reports physical pixels. WinForms positions the menu in its own DPI units.
            double scale = GetDpiForWindow(Handle) / (double)trayDpi;
            return Rectangle.FromLTRB(
                cursor.X + (int)Math.Floor((bounds.Left - physicalCursor.X) * scale),
                cursor.Y + (int)Math.Floor((bounds.Top - physicalCursor.Y) * scale),
                cursor.X + (int)Math.Ceiling((bounds.Right - physicalCursor.X) * scale),
                cursor.Y + (int)Math.Ceiling((bounds.Bottom - physicalCursor.Y) * scale));
        }
        catch (ElementNotAvailableException) { }
        catch (COMException) { }
        catch (Win32Exception) { }
        return Rectangle.Empty;
    }

    [DllImport("user32.dll")]
    private static extern bool GetPhysicalCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPhysicalPoint(Point point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    private void ApplyAppearance(AppearanceMode appearance)
    {
        theme = SettingsTheme.Create(appearance);
        SuspendLayout();
        try
        {
            bool highContrast = theme.HighContrast;
            Renderer = highContrast ? (ToolStripRenderer)systemRenderer : trayRenderer;
            BackColor = highContrast ? Color.Empty : theme.Surface;
            ForeColor = highContrast ? Color.Empty : theme.Text;
            ShowCheckMargin = highContrast;
            int width = Scale(216);
            if (!highContrast)
                foreach (ToolStripItem item in Items)
                    width = Math.Max(width, TextRenderer.MeasureText(item.Text, Font).Width + Scale(TextInset + 38));
            MinimumSize = highContrast ? Size.Empty : new Size(width, 0);
            int height = Math.Max(Scale(36), Font.Height + Scale(16));
            foreach (ToolStripItem item in Items)
            {
                var action = item as ToolStripMenuItem;
                if (action == null) continue;
                action.AutoSize = highContrast;
                if (!highContrast) action.Size = new Size(width - Scale(2), height);
            }
        }
        finally { ResumeLayout(true); }
        ShapeMenu();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ShapeMenu();
    }

    private int Scale(int pixels) { return LogicalToDeviceUnits(pixels); }

    private void ShapeMenu()
    {
        if (theme == null || theme.HighContrast || Width < 2 || Height < 2)
        {
            Region = null;
            return;
        }

        using (var path = SettingsTheme.Rounded(ClientRectangle, Scale(10)))
            Region = new Region(path);
    }

    private sealed class TrayMenuRenderer : ToolStripRenderer
    {
        private readonly TrayMenu menu;

        internal TrayMenuRenderer(TrayMenu menu) { this.menu = menu; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var fill = new SolidBrush(menu.theme.Surface))
                e.Graphics.FillRectangle(fill, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = new Rectangle(0, 0, menu.Width - 1, menu.Height - 1);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var outline = new Pen(menu.theme.Border))
            using (var path = SettingsTheme.Rounded(bounds, menu.Scale(10)))
                e.Graphics.DrawPath(outline, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var item = (ToolStripMenuItem)e.Item;
            var theme = menu.theme;
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            if (item.Selected && item.Enabled)
            {
                var bounds = new Rectangle(Point.Empty, item.Size);
                bounds.Inflate(-menu.Scale(5), -menu.Scale(1));
                using (var highlight = new SolidBrush(theme.Hover))
                using (var path = SettingsTheme.Rounded(bounds, menu.Scale(6)))
                    e.Graphics.FillPath(highlight, path);
            }

            var symbol = (TrayMenuIcon)item.Tag;
            int iconSize = menu.Scale(18);
            int top = (item.Height - iconSize) / 2;
            Color color = symbol == TrayMenuIcon.Gamepad && item.Enabled
                ? theme.Dark ? Color.FromArgb(196, 168, 255) : Color.FromArgb(102, 62, 177)
                : theme.Muted;
            TrayIcons.DrawMenuIcon(e.Graphics, symbol, color, new Rectangle(menu.Scale(13), top, iconSize, iconSize));

            if (item.Checked)
            {
                int x = item.Width - menu.Scale(CheckInset);
                int y = item.Height / 2;
                using (var pen = new Pen(item.Enabled ? theme.Accent : theme.Muted, menu.Scale(2))
                    { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    e.Graphics.DrawLines(pen, new[] { new Point(x - menu.Scale(4), y),
                        new Point(x - menu.Scale(1), y + menu.Scale(3)), new Point(x + menu.Scale(5), y - menu.Scale(3)) });
            }
            e.Graphics.Restore(state);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            int x = menu.Scale(TextInset);
            int width = e.Item.Width - x - menu.Scale(CheckInset + 10);
            e.TextRectangle = new Rectangle(x, 0, width, e.Item.Height);
            e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsClipping;
            e.TextColor = e.Item.Enabled ? menu.theme.Text : menu.theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var divider = new Pen(menu.theme.Border))
                e.Graphics.DrawLine(divider, menu.Scale(11), y, e.Item.Width - menu.Scale(11), y);
        }
    }
}
