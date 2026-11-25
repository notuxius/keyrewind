using Microsoft.Win32;
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal enum AppearanceMode { System, Light, Dark }
internal enum SettingsButtonStyle { Secondary, Primary, Navigation }

internal sealed class SettingsTheme
{
    public Color Background, Rail, Surface, Text, Muted, Border, Field, Hover, Accent,
        AccentHover, AccentText, Selection, SelectionText;
    public bool Dark, HighContrast;

    public static SettingsTheme Create(AppearanceMode mode)
    {
        if (SystemInformation.HighContrast)
        {
            return new SettingsTheme
            {
                HighContrast = true, Background = SystemColors.Window, Rail = SystemColors.Control,
                Surface = SystemColors.Window, Text = SystemColors.WindowText, Muted = SystemColors.WindowText,
                Border = SystemColors.WindowText, Field = SystemColors.Window, Hover = SystemColors.Control,
                Accent = SystemColors.Highlight, AccentHover = SystemColors.Highlight,
                AccentText = SystemColors.HighlightText, Selection = SystemColors.Highlight,
                SelectionText = SystemColors.HighlightText
            };
        }

        bool dark = mode == AppearanceMode.Dark;
        if (mode == AppearanceMode.System)
        {
            const string personalize = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            dark = Equals(Registry.GetValue(personalize, "AppsUseLightTheme", 1), 0);
        }

        return dark
            ? new SettingsTheme
            {
                Dark = true, Background = Hex(0x13171F), Rail = Hex(0x181D27), Surface = Hex(0x1D232E),
                Text = Hex(0xF1F4FC), Muted = Hex(0xAEB9CC), Border = Hex(0x3C475B),
                Field = Hex(0x171C25), Hover = Hex(0x2B3546), Accent = Hex(0x8ADCCD),
                AccentHover = Hex(0xA6E7DB), AccentText = Hex(0x102925), Selection = Hex(0x263C41),
                SelectionText = Hex(0xA6E7DB)
            }
            : new SettingsTheme
            {
                Background = Hex(0xF3F6FA), Rail = Hex(0xE9EFF6), Surface = Color.White,
                Text = Hex(0x172337), Muted = Hex(0x4D6079), Border = Hex(0xCBD6E3),
                Field = Hex(0xF7F9FC), Hover = Hex(0xEDF3F8), Accent = Hex(0x176D61),
                AccentHover = Hex(0x125B51), AccentText = Color.White, Selection = Hex(0xD3EAE6),
                SelectionText = Hex(0x11574E)
            };
    }

    private static Color Hex(int rgb)
    {
        return Color.FromArgb(unchecked((int)0xff000000) | rgb);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetWindowTheme(IntPtr window, string application, string classes);

    internal static void DisableWindowTransitions(IntPtr handle)
    {
        int disabled = 1;
        DwmSetWindowAttribute(handle, 3, ref disabled, sizeof(int)); // DWMWA_TRANSITIONS_FORCEDISABLED
    }

    internal static void SetWindowCloaked(IntPtr handle, bool cloaked)
    {
        if (!cloaked) DwmFlush();
        int value = cloaked ? 1 : 0;
        DwmSetWindowAttribute(handle, 13, ref value, sizeof(int)); // DWMWA_CLOAK
    }

    internal void ApplyCaption(IntPtr handle)
    {
        int dark = Dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        int border = HighContrast ? -1 : ColorTranslator.ToWin32(Border);
        int caption = HighContrast ? -1 : ColorTranslator.ToWin32(Rail);
        int text = HighContrast ? -1 : ColorTranslator.ToWin32(Text);
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }

    internal void Apply(Control control)
    {
        control.ForeColor = Text;
        if (HasScrollbars(control)) ApplyScrollbars(control);
        var panel = control as SettingsPanel;
        if (panel != null)
        {
            panel.Theme = this;
            panel.BackColor = panel.IsRail ? Rail : panel.IsCard ? Background : control.Parent.BackColor;
            panel.Invalidate();
        }
        else if (control is TextBoxBase)
        {
            control.BackColor = Field;
        }
        else if (control is SettingsButton)
        {
            ((SettingsButton)control).Theme = this;
            control.Invalidate();
        }
        else if (control is SettingsCheckBox)
        {
            ((SettingsCheckBox)control).Theme = this;
            control.Invalidate();
        }
        else if (control is DataGridView)
        {
            var grid = (DataGridView)control;
            grid.BackgroundColor = Field;
            grid.GridColor = Border;
            grid.DefaultCellStyle.BackColor = Field;
            grid.DefaultCellStyle.ForeColor = Text;
            grid.DefaultCellStyle.SelectionBackColor = Selection;
            grid.DefaultCellStyle.SelectionForeColor = SelectionText;
            grid.ColumnHeadersDefaultCellStyle.BackColor = Rail;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Rail;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        }
        else if (control is Label && (control.Tag as string) == "muted")
        {
            control.ForeColor = Muted;
        }
        foreach (Control child in control.Controls)
        {
            if (panel != null && panel.IsCard && child is Panel)
                child.BackColor = Surface;
            Apply(child);
        }
    }

    internal static bool HasScrollbars(Control control)
    {
        var scrollable = control as ScrollableControl;
        return control is ScrollBar || scrollable != null && scrollable.AutoScroll;
    }

    internal void ApplyScrollbars(Control control)
    {
        if (control.IsHandleCreated)
        {
            string style = HighContrast ? null : Dark ? "DarkMode_Explorer" : "Explorer";
            SetWindowTheme(control.Handle, style, HighContrast ? null : "ScrollBar");
        }
    }

    internal void ApplyMenu(ContextMenuStrip menu, SettingsDropdown dropdown = null)
    {
        bool systemMenu = HighContrast && dropdown == null;
        menu.Renderer = systemMenu ? (ToolStripRenderer)new ToolStripSystemRenderer() : new MenuRenderer(this, dropdown);
        menu.BackColor = systemMenu ? Color.Empty : dropdown == null ? Surface : Field;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = systemMenu ? Color.Empty : Text;
    }

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly SettingsTheme theme;
        private readonly SettingsDropdown dropdown;

        internal MenuRenderer(SettingsTheme theme, SettingsDropdown dropdown) :
            base(new MenuColors(theme, dropdown == null ? theme.Surface : theme.Field))
        {
            this.theme = theme;
            this.dropdown = dropdown;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (dropdown == null)
            {
                base.OnRenderToolStripBorder(e);
                return;
            }

            bool above = dropdown.OpensAbove;
            var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(bounds, dropdown.LogicalToDeviceUnits(7), above, !above))
            using (var outline = new Pen(theme.Accent))
                e.Graphics.DrawPath(outline, path);
            e.Graphics.Restore(state);

            int seam = above ? bounds.Bottom : bounds.Top;
            using (var divider = new Pen(theme.Border))
                e.Graphics.DrawLine(divider, 1, seam, bounds.Right - 1, seam);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (dropdown == null)
            {
                base.OnRenderMenuItemBackground(e);
                return;
            }

            if (!e.Item.Selected) return;
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            bounds.Inflate(-dropdown.LogicalToDeviceUnits(4), -1);
            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Rounded(bounds, dropdown.LogicalToDeviceUnits(4)))
            using (var fill = new SolidBrush(theme.Selection))
                e.Graphics.FillPath(fill, path);
            e.Graphics.Restore(state);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (dropdown != null)
            {
                e.TextFormat |= TextFormatFlags.EndEllipsis;
                if (e.Item.Selected) e.TextColor = theme.SelectionText;
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var bounds = e.ImageRectangle;
            int x = bounds.Left + bounds.Width / 2;
            int y = bounds.Top + bounds.Height / 2;
            Color color = dropdown != null && e.Item.Selected ? theme.SelectionText : theme.Accent;
            using (var pen = new Pen(color, 2))
                e.Graphics.DrawLines(pen, new[] { new Point(x - 4, y), new Point(x - 1, y + 3), new Point(x + 4, y - 3) });
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly SettingsTheme theme;
        private readonly Color background;

        internal MenuColors(SettingsTheme theme, Color background) { this.theme = theme; this.background = background; }

        public override Color ToolStripDropDownBackground { get { return background; } }
        public override Color MenuBorder { get { return theme.Border; } }
        public override Color MenuItemBorder { get { return theme.Border; } }
        public override Color MenuItemSelected { get { return theme.Hover; } }
        public override Color SeparatorDark { get { return theme.Border; } }
        public override Color SeparatorLight { get { return theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return background; } }
        public override Color ImageMarginGradientMiddle { get { return background; } }
        public override Color ImageMarginGradientEnd { get { return background; } }
    }

    internal static GraphicsPath Rounded(Rectangle bounds, int radius, bool roundTop = true, bool roundBottom = true)
    {
        var path = new GraphicsPath();
        int diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter < 2)
        {
            path.AddRectangle(bounds);
            return path;
        }
        if (roundTop)
        {
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        }
        else path.AddLine(bounds.Left, bounds.Top, bounds.Right, bounds.Top);
        if (roundBottom)
        {
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        }
        else path.AddLine(bounds.Right, bounds.Bottom, bounds.Left, bounds.Bottom);
        path.CloseFigure();
        return path;
    }
}

internal sealed class SettingsPanel : Panel
{
    internal SettingsTheme Theme;
    internal bool IsCard { get; set; }
    internal bool IsRail { get; set; }

    public SettingsPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Theme == null || !IsCard || Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var border = new Pen(Theme.Border))
        using (var surface = new SolidBrush(Theme.Surface))
        using (var path = SettingsTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), ScaleDip(12)))
        {
            e.Graphics.FillPath(surface, path);
            e.Graphics.DrawPath(border, path);
        }
    }

    private int ScaleDip(int value) { return (int)Math.Round(value * DeviceDpi / 96.0); }
}

internal class SettingsButton : Button
{
    internal SettingsTheme Theme;
    internal SettingsButtonStyle Appearance { get; set; }
    internal string Glyph { get; set; }
    private bool hovered, pressed, selected;

    internal bool Selected
    {
        get { return selected; }
        set { selected = value; Invalidate(); }
    }

    public SettingsButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Theme == null)
        {
            base.OnPaint(e);
            return;
        }

        bool primary = Appearance == SettingsButtonStyle.Primary && Enabled;
        bool navigation = Appearance == SettingsButtonStyle.Navigation;
        var dropdown = this as SettingsDropdown;
        bool expanded = dropdown != null && dropdown.Expanded;
        bool active = Selected && Enabled;
        Color parent = Parent.BackColor;
        Color background = navigation ? parent : Theme.Field;
        Color foreground = Enabled ? Theme.Text : Theme.Muted;
        if (primary)
        {
            background = hovered || pressed ? Theme.AccentHover : Theme.Accent;
            foreground = Theme.AccentText;
        }
        else if (active)
        {
            background = Theme.Selection;
            foreground = Theme.SelectionText;
        }
        else if (Enabled && !expanded && (hovered || pressed))
        {
            background = Theme.Hover;
        }

        e.Graphics.Clear(parent);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = SettingsTheme.Rounded(bounds, ScaleDip(7),
            !expanded || !dropdown.OpensAbove, !expanded || dropdown.OpensAbove))
        using (var fill = new SolidBrush(background))
        {
            e.Graphics.FillPath(fill, path);
            if (!navigation && !primary)
            {
                using (var stroke = new Pen(active || expanded ? Theme.Accent : Theme.Border))
                    e.Graphics.DrawPath(stroke, path);
            }
        }

        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
        if (dropdown != null)
        {
            int inset = ScaleDip(12);
            int arrowX = Width - inset - ScaleDip(5);
            int centerY = Height / 2;
            int arrowRise = ScaleDip(2) * (expanded ? -1 : 1);
            using (var pen = new Pen(expanded ? Theme.Accent : foreground, ScaleDip(1)))
                e.Graphics.DrawLines(pen, new[] { new Point(arrowX - ScaleDip(4), centerY - arrowRise),
                    new Point(arrowX, centerY + arrowRise), new Point(arrowX + ScaleDip(4), centerY - arrowRise) });
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset, 0, Math.Max(0, arrowX - inset * 2), Height),
                foreground, flags | TextFormatFlags.Left);
        }
        else if (Glyph != null)
        {
            bool hasText = Text.Length != 0;
            int inset = ScaleDip(12);
            int iconWidth = ScaleDip(21);
            var iconBounds = hasText ? new Rectangle(inset, 0, iconWidth, Height) : ClientRectangle;
            using (var font = new Font("Segoe MDL2 Assets", 11))
                TextRenderer.DrawText(e.Graphics, Glyph, font, iconBounds, foreground,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            if (hasText)
            {
                var textBounds = new Rectangle(inset + iconWidth + ScaleDip(7), 0,
                    Math.Max(0, Width - inset * 2 - iconWidth - ScaleDip(7)), Height);
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds, foreground, flags | TextFormatFlags.Left);
            }
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground, flags | TextFormatFlags.HorizontalCenter);
        }
        if (Focused && ShowFocusCues && !expanded)
        {
            bounds.Inflate(-ScaleDip(3), -ScaleDip(3));
            ControlPaint.DrawFocusRectangle(e.Graphics, bounds, foreground, background);
        }
    }

    private int ScaleDip(int value) { return (int)Math.Round(value * DeviceDpi / 96.0); }
}
