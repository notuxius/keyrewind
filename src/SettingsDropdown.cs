using System;
using System.Drawing;
using System.Windows.Forms;

internal sealed class SettingsDropdown : SettingsButton
{
    private readonly ContextMenuStrip options = new ContextMenuStrip
    {
        ShowImageMargin = false, ShowCheckMargin = true, DropShadowEnabled = false
    };

    internal int SelectedIndex { get; private set; }
    internal bool Expanded { get { return options.Visible; } }
    internal bool OpensAbove { get; private set; }

    internal SettingsDropdown(string name)
    {
        AccessibleName = name;
        AccessibleRole = AccessibleRole.ButtonDropDown;
        Dock = DockStyle.Fill;
        options.AccessibleName = name;
        options.Opening += delegate { Theme.ApplyMenu(options, this); };
        options.Opened += delegate { ShapePopup(); Invalidate(); };
        options.Closed += delegate { Invalidate(); };
        options.SizeChanged += delegate { if (Expanded) ShapePopup(); };
    }

    internal void AddOption(string text)
    {
        int index = options.Items.Count;
        var item = new ToolStripMenuItem { Text = text.Replace("&", "&&"), Tag = text };
        item.Click += delegate { SelectOption(index); };
        options.Items.Add(item);
    }

    internal void SelectOption(int index)
    {
        ((ToolStripMenuItem)options.Items[SelectedIndex]).Checked = false;
        SelectedIndex = index;
        var item = (ToolStripMenuItem)options.Items[index];
        item.Checked = true;
        Text = (string)item.Tag;
        AccessibleDescription = Text;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Expanded)
        {
            options.Close();
            return;
        }

        options.Font = Font;
        options.MinimumSize = options.MaximumSize = new Size(Width, 0);
        foreach (ToolStripItem item in options.Items)
        {
            item.AutoSize = false;
            item.Width = Math.Max(1, Width - 2);
            item.Height = Math.Max(item.GetPreferredSize(Size.Empty).Height, LogicalToDeviceUnits(27));
        }

        Rectangle field = RectangleToScreen(ClientRectangle);
        Rectangle workArea = Screen.FromRectangle(field).WorkingArea;
        int below = workArea.Bottom - field.Bottom;
        int above = field.Top - workArea.Top;
        OpensAbove = options.GetPreferredSize(Size.Empty).Height > below && above > below;
        int available = Math.Max(1, (OpensAbove ? above : below) + 1);
        options.MaximumSize = new Size(Width, available);
        options.Show(this, new Point(0, OpensAbove ? 1 : Height - 1),
            OpensAbove ? ToolStripDropDownDirection.AboveRight : ToolStripDropDownDirection.BelowRight);
        options.Items[SelectedIndex].Select();
    }

    private void ShapePopup()
    {
        using (var path = SettingsTheme.Rounded(options.ClientRectangle, LogicalToDeviceUnits(7), OpensAbove, !OpensAbove))
            options.Region = new Region(path);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys data)
    {
        if (data == Keys.Down || data == (Keys.Alt | Keys.Down) || data == Keys.F4)
        {
            OnClick(EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref message, data);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) options.Dispose();
        base.Dispose(disposing);
    }
}
