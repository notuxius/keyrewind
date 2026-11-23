using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

internal sealed class SettingsCheckBox : CheckBox
{
    internal const int LabelInset = 30;
    internal SettingsTheme Theme;
    private bool hovered;

    internal SettingsCheckBox(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Theme == null)
        {
            base.OnPaint(e);
            return;
        }

        int size = LogicalToDeviceUnits(18);
        var box = new Rectangle(LogicalToDeviceUnits(4), (Height - size) / 2, size, size);
        bool hot = Enabled && hovered;
        Color muted = Theme.HighContrast ? SystemColors.GrayText : Theme.Muted;
        Color fill = hot ? Theme.Hover : Theme.Field;
        Color border = Enabled ? Theme.Muted : muted;
        if (Checked)
        {
            fill = Enabled ? (hot ? Theme.AccentHover : Theme.Accent) : Theme.Hover;
            border = Enabled ? fill : muted;
        }
        else if (hot) border = Theme.Accent;

        var graphics = e.Graphics;
        graphics.Clear(BackColor);
        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = SettingsTheme.Rounded(box, LogicalToDeviceUnits(4)))
        using (var brush = new SolidBrush(fill))
        using (var stroke = new Pen(border, LogicalToDeviceUnits(1)))
        {
            graphics.FillPath(brush, path);
            graphics.DrawPath(stroke, path);
        }

        if (Checked)
        {
            using (var pen = new Pen(Enabled ? Theme.AccentText : muted, LogicalToDeviceUnits(2))
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            })
            {
                int left = box.Left + LogicalToDeviceUnits(5);
                int right = box.Left + LogicalToDeviceUnits(13);
                int middle = box.Top + size / 2;
                if (CheckState == CheckState.Indeterminate)
                    graphics.DrawLine(pen, left, middle, right, middle);
                else
                    graphics.DrawLines(pen, new[] { new Point(left, middle),
                        new Point(box.Left + LogicalToDeviceUnits(8), box.Top + LogicalToDeviceUnits(12)),
                        new Point(right, box.Top + LogicalToDeviceUnits(6)) });
            }
        }

        if (Focused && ShowFocusCues)
        {
            box.Inflate(LogicalToDeviceUnits(3), LogicalToDeviceUnits(3));
            using (var path = SettingsTheme.Rounded(box, LogicalToDeviceUnits(6)))
            using (var pen = new Pen(Theme.Accent, LogicalToDeviceUnits(1)))
                graphics.DrawPath(pen, path);
        }
        graphics.Restore(state);

        int inset = LogicalToDeviceUnits(LabelInset);
        var label = new Rectangle(inset, 0, Math.Max(0, Width - inset), Height);
        TextRenderer.DrawText(graphics, Text, Font, label, Enabled ? Theme.Text : muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
