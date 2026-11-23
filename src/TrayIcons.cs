using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

internal enum TrayMenuIcon { Settings, Gamepad, Exit }

internal static class TrayIcons
{
    internal static readonly Color GamingViolet = Color.FromArgb(137, 91, 229);

    internal static Icon WithGamingBadge(Icon icon)
    {
        // Read the decoded Windows icon. .NET Framework can misread PNG-backed ICO data in ToBitmap().
        using (var decodedIcon = Icon.FromHandle(icon.Handle))
        using (var bitmap = decodedIcon.ToBitmap())
        {
            float diameter = Math.Min(bitmap.Width, bitmap.Height) * (6f / 16);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var dot = new SolidBrush(GamingViolet))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.FillEllipse(dot, bitmap.Width - diameter, bitmap.Height - diameter, diameter, diameter);
            }
            using (var png = new MemoryStream())
            {
                // Load the PNG through Windows so the icon keeps the original alpha blending.
                bitmap.Save(png, ImageFormat.Png);
                byte[] image = png.ToArray();
                var handle = CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, bitmap.Width, bitmap.Height, 0);
                if (handle == IntPtr.Zero) throw new Win32Exception();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

    internal static void DrawMenuIcon(Graphics graphics, TrayMenuIcon symbol, Color color, Rectangle bounds)
    {
        var state = graphics.Save();
        using (var pen = new Pen(color, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TranslateTransform(bounds.Left, bounds.Top);
            graphics.ScaleTransform(bounds.Width / 16f, bounds.Height / 16f);
            switch (symbol)
            {
                case TrayMenuIcon.Settings:
                    graphics.DrawLine(pen, 2.5f, 5, 4.5f, 5);
                    graphics.DrawEllipse(pen, 4.5f, 3.5f, 3, 3);
                    graphics.DrawLine(pen, 7.5f, 5, 13.5f, 5);
                    graphics.DrawLine(pen, 2.5f, 11, 8.5f, 11);
                    graphics.DrawEllipse(pen, 8.5f, 9.5f, 3, 3);
                    graphics.DrawLine(pen, 11.5f, 11, 13.5f, 11);
                    break;
                case TrayMenuIcon.Gamepad:
                    DrawGamepad(graphics, pen);
                    break;
                case TrayMenuIcon.Exit:
                    graphics.DrawLines(pen, new[] { new Point(7, 2), new Point(3, 2), new Point(3, 14), new Point(7, 14) });
                    graphics.DrawLine(pen, 6.5f, 8, 13.5f, 8);
                    graphics.DrawLines(pen, new[] { new PointF(10.5f, 5), new PointF(13.5f, 8), new PointF(10.5f, 11) });
                    break;
            }
        }
        graphics.Restore(state);
    }

    private static void DrawGamepad(Graphics graphics, Pen pen)
    {
        using (var body = new GraphicsPath())
        {
            body.AddLine(5, 4, 11, 4);
            body.AddBezier(11, 4, 13.5f, 4, 14.5f, 7.5f, 14.5f, 10.5f);
            body.AddBezier(14.5f, 10.5f, 14.5f, 13, 12.8f, 13.5f, 11.3f, 12);
            body.AddLines(new[] { new PointF(11.3f, 12), new PointF(9.8f, 10.5f),
                new PointF(6.2f, 10.5f), new PointF(4.7f, 12) });
            body.AddBezier(4.7f, 12, 3.2f, 13.5f, 1.5f, 13, 1.5f, 10.5f);
            body.AddBezier(1.5f, 10.5f, 1.5f, 7.5f, 2.5f, 4, 5, 4);
            body.CloseFigure();
            graphics.DrawPath(pen, body);
        }
        graphics.DrawLine(pen, 4.5f, 7.6f, 7, 7.6f);
        graphics.DrawLine(pen, 5.75f, 6.35f, 5.75f, 8.85f);
        using (var button = new SolidBrush(pen.Color))
            graphics.FillEllipse(button, 10.2f, 6.8f, 1.6f, 1.6f);
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint length, bool icon, uint version,
        int width, int height, uint flags);
}
