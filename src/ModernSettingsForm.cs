using Microsoft.Win32;
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal sealed class ModernSettingsForm : Form
{
    private const string InvalidShortcutMessage = "Use a modifier key with your shortcut, or choose a function key.";
    private readonly Action<Settings> apply;
    private readonly Settings original;
    private readonly KeyboardEngine engine;
    private readonly Font bodyFont = new Font("Segoe UI", 10);
    private readonly Font titleFont = new Font("Segoe UI", 25, FontStyle.Bold);
    private readonly Font headingFont = new Font("Segoe UI", 12, FontStyle.Bold);
    private readonly SettingsButton[] tabs = new SettingsButton[4];
    private readonly Panel[] pages = new Panel[4];
    private readonly SettingsButton[] themes = new SettingsButton[3];
    private readonly SettingsDropdown layoutA, layoutB;
    private readonly SettingsCheckBox autoswitchPopups = new SettingsCheckBox("Show autoswitch popups");
    private readonly DataGridView snippets = new DataGridView();
    private readonly ContextMenuStrip textMenu = new ContextMenuStrip { ShowImageMargin = false };
    private readonly Hotkey defaultConvert = Settings.CreateDefaultConvert();
    private readonly ToolTip shortcutTips = new ToolTip();
    private readonly SettingsButton convertButton, expandButton, resetConvert, resetExpand;
    private readonly SettingsCheckBox launchInBackground = new SettingsCheckBox("Launch in background");
    private readonly SettingsCheckBox closeToTray = new SettingsCheckBox("Close to tray");
    private readonly Label status = TextLabel("", true);
    private readonly InputLanguage[] installed;
    private SettingsTheme theme;
    private AppearanceMode appearance;
    private Hotkey convert, expand;
    private SettingsButton recording;
    private FormWindowState unminimizedState;

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr updateRectangle, IntPtr updateRegion, uint flags);

    internal ModernSettingsForm(Settings settings, KeyboardEngine engine, Icon appIcon, Action<Settings> apply)
    {
        this.apply = apply;
        this.engine = engine;
        original = settings;
        appearance = settings.Appearance;
        convert = settings.Convert;
        expand = settings.Expand;
        installed = LayoutConverter.Installed().GroupBy(layout => layout.Handle).Select(group => group.First()).ToArray();

        Text = Program.Name;
        Icon = appIcon;
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = bodyFont;
        ClientSize = new Size(930, 625);
        MinimumSize = new Size(742, 516);
        KeyPreview = true;

        var shell = Table(2);
        shell.Dock = DockStyle.Fill;
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 204));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        var rail = new SettingsPanel { IsRail = true, Dock = DockStyle.Fill, Padding = new Padding(16), Margin = Padding.Empty };
        shell.Controls.Add(rail, 0, 0);
        var menu = Table(1);
        menu.Dock = DockStyle.Top;
        rail.Controls.Add(menu);
        AddRow(menu, Nav("Shortcuts", "\uE765", 0), 43, 4);
        AddRow(menu, Nav("Snippets", "\uE8C8", 1), 43, 4);
        AddRow(menu, Nav("Appearance", "\uE706", 2), 43, 4);
        AddRow(menu, Nav("General", "\uE713", 3), 43, 4);

        var workspace = Table(1);
        workspace.Dock = DockStyle.Fill;
        workspace.AutoSize = false;
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        shell.Controls.Add(workspace, 1, 0);
        var viewport = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        workspace.Controls.Add(viewport, 0, 0);
        var shortcuts = Page(viewport, 0, "Shortcuts", "Choose your keyboard layouts and the keys that trigger each action.");
        var snippetPage = Page(viewport, 1, "Snippets", "Type an abbreviation and use your snippet shortcut.");
        var appearancePage = Page(viewport, 2, "Appearance", "Preview a theme before saving it.");
        var generalPage = Page(viewport, 3, "General", "Reopen Settings from the tray icon.");

        layoutA = new SettingsDropdown("First keyboard layout");
        layoutB = new SettingsDropdown("Second keyboard layout");
        autoswitchPopups.Checked = settings.ShowAutoswitchPopups;
        AddRow(shortcuts, BuildLayoutCard(), 179, 14);
        TableLayoutPanel shortcutRows;
        var shortcutCard = Card(out shortcutRows);
        AddRow(shortcutRows, Heading("Keyboard shortcuts"), 30, 0);
        resetConvert = ResetShortcutButton("Reset conversion shortcut to " + defaultConvert,
            delegate { StopRecording(); convert = defaultConvert; RefreshShortcuts(); });
        resetExpand = ResetShortcutButton("Reset snippet shortcut to Double Space",
            delegate { StopRecording(); expand = null; RefreshShortcuts(); });
        AddRow(shortcutRows, ShortcutRow("Convert selection", resetConvert, out convertButton), 44, 3);
        AddRow(shortcutRows, ShortcutRow("Expand snippet", resetExpand, out expandButton), 44, 3);
        convertButton.Click += delegate { StartRecording(convertButton); };
        expandButton.Click += delegate { StartRecording(expandButton); };
        convertButton.LostFocus += delegate { StopRecording(); };
        expandButton.LostFocus += delegate { StopRecording(); };
        AddRow(shortcuts, shortcutCard, 158, 0);
        RefreshShortcuts();

        BuildSnippets(snippetPage, settings);
        BuildAppearance(appearancePage);
        BuildGeneral(generalPage, settings);

        var footer = Table(2);
        footer.Dock = DockStyle.Fill;
        footer.Padding = new Padding(34, 10, 34, 14);
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 143));
        footer.Controls.Add(status, 0, 0);
        var save = Button("Save changes", null, delegate { SaveChanges(); });
        save.Appearance = SettingsButtonStyle.Primary;
        footer.Controls.Add(save, 1, 0);
        workspace.Controls.Add(footer, 0, 1);

        ShowPage(0);
        TrackScrollbars(this);
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += UserPreferenceChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            const int composited = 0x02000000; // WS_EX_COMPOSITED buffers the child controls with the form.
            parameters.ExStyle |= composited;
            return parameters;
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        SettingsTheme.SetWindowCloaked(Handle, true);
        var saved = original.SettingsWindowBounds;
        var screen = saved.Width > 0 && saved.Height > 0 ? Screen.FromRectangle(saved) : Screen.FromPoint(Cursor.Position);
        var area = screen.WorkingArea;
        MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
        Bounds = FitWindowBounds(saved, area, MinimumSize, Size);
        if (original.SettingsWindowMaximized) WindowState = FormWindowState.Maximized;
        base.OnLoad(e);
    }

    protected override void OnShown(EventArgs e)
    {
        const uint redrawAllNow = 0x0001 | 0x0004 | 0x0080 | 0x0100; // Invalidate, erase, and paint every child synchronously.
        RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, redrawAllNow);
        SettingsTheme.SetWindowCloaked(Handle, false);
        Activate();
        base.OnShown(e);
    }

    internal static Rectangle FitWindowBounds(Rectangle saved, Rectangle area, Size minimum, Size defaultSize)
    {
        bool hasSavedSize = saved.Width > 0 && saved.Height > 0;
        var size = hasSavedSize ? saved.Size : defaultSize;
        int width = Math.Min(area.Width, Math.Max(minimum.Width, size.Width));
        int height = Math.Min(area.Height, Math.Max(minimum.Height, size.Height));
        if (hasSavedSize && area.IntersectsWith(saved))
        {
            int left = Math.Max(area.Left, Math.Min(saved.Left, area.Right - width));
            int top = Math.Max(area.Top, Math.Min(saved.Top, area.Bottom - height));
            return new Rectangle(left, top, width, height);
        }
        return new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
    }

    protected override void OnResize(EventArgs e)
    {
        if (WindowState != FormWindowState.Minimized) unminimizedState = WindowState;
        base.OnResize(e);
    }

    internal void BringForward()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = unminimizedState;
        Activate();
    }

    internal bool RememberPlacement(Settings settings)
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        bool maximized = unminimizedState == FormWindowState.Maximized;
        if (bounds == settings.SettingsWindowBounds && maximized == settings.SettingsWindowMaximized) return false;
        settings.SettingsWindowBounds = bounds;
        settings.SettingsWindowMaximized = maximized;
        return true;
    }

    private SettingsButton Nav(string text, string glyph, int index)
    {
        var button = Button(text, glyph, delegate { ShowPage(index); });
        button.Appearance = SettingsButtonStyle.Navigation;
        tabs[index] = button;
        return button;
    }

    private void ShowPage(int index)
    {
        StopRecording();
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i].Visible = i == index;
            tabs[i].Selected = i == index;
        }
    }

    private TableLayoutPanel Page(Panel host, int index, string title, string description)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(34, 22, 34, 8) };
        pages[index] = page;
        host.Controls.Add(page);
        var content = Table(1);
        content.Dock = DockStyle.Top;
        page.Controls.Add(content);
        var heading = TextLabel(title, false);
        heading.Font = titleFont;
        AddRow(content, heading, 51, 0);
        AddRow(content, TextLabel(description, true), 36, 13);
        return content;
    }

    private Control BuildLayoutCard()
    {
        TableLayoutPanel rows;
        var card = Card(out rows);
        AddRow(rows, Heading("Autoswitch layouts"), 27, 0);
        bool canChoose = installed.Length > 2;
        string description = installed.Length == 2 ? "Both installed layouts are selected automatically."
            : canChoose ? "Choose which two layouts the conversion shortcut switches between."
            : "Install a second keyboard layout in Windows to use conversion.";
        AddRow(rows, TextLabel(description, true), 31, 4);
        if (!canChoose)
        {
            string names = string.Join("   ↔   ", installed.Select(layout => layout.LayoutName));
            AddRow(rows, Heading(names), 41, 0);
        }
        else
        {
            IntPtr first, second;
            if (!LayoutConverter.GetPair(installed.Select(layout => layout.Handle), original, out first, out second))
                first = second = IntPtr.Zero;
            FillLayout(layoutA, first);
            FillLayout(layoutB, second);
            var selectors = Table(2);
            selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layoutA.Margin = new Padding(0, 2, 7, 0);
            layoutB.Margin = new Padding(7, 2, 0, 0);
            selectors.Controls.Add(layoutA, 0, 0);
            selectors.Controls.Add(layoutB, 1, 0);
            AddRow(rows, selectors, 41, 0);
        }
        AddRow(rows, autoswitchPopups, 31, 0);
        return card;
    }

    private void FillLayout(SettingsDropdown picker, IntPtr selected)
    {
        picker.AddOption("Choose a layout");
        foreach (var layout in installed) picker.AddOption(layout.Culture.DisplayName + " · " + layout.LayoutName);
        picker.SelectOption(Array.FindIndex(installed, layout => layout.Handle == selected) + 1);
    }

    private int SelectedLayout(SettingsDropdown picker)
    {
        return picker.SelectedIndex <= 0 ? 0 : LayoutConverter.LayoutId(installed[picker.SelectedIndex - 1].Handle);
    }

    private static TableLayoutPanel ShortcutRow(string title, SettingsButton reset, out SettingsButton shortcut)
    {
        var row = Table(3);
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        row.Controls.Add(TextLabel(title, false), 0, 0);
        shortcut = Button("", null, null);
        shortcut.AccessibleName = "Record shortcut for " + title.ToLowerInvariant();
        row.Controls.Add(shortcut, 1, 0);
        row.Controls.Add(reset, 2, 0);
        return row;
    }

    private SettingsButton ResetShortcutButton(string description, EventHandler click)
    {
        var button = Button("", "\uE7A7", click);
        button.Margin = new Padding(8, 0, 0, 0);
        button.AccessibleName = description;
        shortcutTips.SetToolTip(button, description);
        return button;
    }

    private void RefreshShortcuts()
    {
        convertButton.Text = convert.ToString();
        convertButton.AccessibleDescription = convertButton.Text;
        expandButton.Text = expand == null ? "Double Space" : expand.ToString();
        expandButton.AccessibleDescription = expandButton.Text;
        resetConvert.Enabled = !convert.Matches(defaultConvert.Key, defaultConvert.Modifiers);
        resetExpand.Enabled = expand != null;
    }

    private void StartRecording(SettingsButton button)
    {
        StopRecording();
        recording = button;
        recording.Text = "Press keys…";
        status.Text = "Press the shortcut you want to use, or press Esc to cancel.";
        if (engine.Enabled)
        {
            engine.Recorded = hotkey =>
            {
                if (recording == button && !IsDisposed) CompleteRecording(hotkey);
            };
        }
    }

    private void CompleteRecording(Hotkey hotkey)
    {
        if (recording == convertButton) convert = hotkey;
        else expand = hotkey;
        StopRecording();
    }

    internal void StopRecording()
    {
        if (recording == null) return;
        engine.Recorded = null;
        recording = null;
        RefreshShortcuts();
        status.Text = "";
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (recording == null) return base.ProcessCmdKey(ref message, keyData);
        if (keyData == Keys.Escape)
        {
            StopRecording();
            return true;
        }

        int key = (int)(keyData & Keys.KeyCode);
        if (engine.Enabled || WindowsInput.ModifierKey(key)) return base.ProcessCmdKey(ref message, keyData);

        // While hooks are paused, record only keyboard messages delivered to Settings.
        const long previousKeyDown = 1L << 30;
        if ((message.LParam.ToInt64() & previousKeyDown) != 0) return true;

        bool control = (keyData & Keys.Control) != 0;
        bool altGr = control && WindowsInput.GetKeyState((int)Keys.RMenu) < 0;
        bool win = WindowsInput.GetKeyState((int)Keys.LWin) < 0 || WindowsInput.GetKeyState((int)Keys.RWin) < 0;
        int modifiers = ((keyData & Keys.Shift) != 0 ? WindowsInput.Shift : 0) |
            (control ? WindowsInput.Control : 0) |
            ((keyData & Keys.Alt) != 0 ? WindowsInput.Alt : 0) |
            (win ? WindowsInput.Win : 0);
        var hotkey = new Hotkey { Key = key, Modifiers = modifiers };
        if (!altGr && key != (int)Keys.CapsLock && hotkey.Valid()) CompleteRecording(hotkey);
        else status.Text = InvalidShortcutMessage;
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (recording != null && !WindowsInput.ModifierKey(e.KeyValue))
        {
            e.SuppressKeyPress = true;
            status.Text = InvalidShortcutMessage;
        }
        base.OnKeyDown(e);
    }

    private void BuildSnippets(TableLayoutPanel page, Settings settings)
    {
        TableLayoutPanel rows;
        var card = Card(out rows);
        var actions = Table(3);
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 113));
        actions.Controls.Add(Heading("Saved snippets"), 0, 0);
        var remove = Button("Remove", null, delegate
        {
            if (snippets.CurrentRow != null) snippets.Rows.Remove(snippets.CurrentRow);
        });
        remove.AccessibleName = "Remove selected snippet";
        remove.Margin = new Padding(0, 0, 7, 0);
        actions.Controls.Add(remove, 1, 0);
        var add = Button("Add snippet", null, delegate
        {
            snippets.CurrentCell = snippets.Rows[snippets.Rows.Add()].Cells[0];
            snippets.Focus();
            snippets.BeginEdit(true);
        });
        add.Appearance = SettingsButtonStyle.Primary;
        actions.Controls.Add(add, 2, 0);
        AddRow(rows, actions, 36, 10);

        snippets.AccessibleName = "Snippets";
        snippets.AllowUserToAddRows = false;
        snippets.AllowUserToResizeRows = false;
        snippets.AllowUserToOrderColumns = false;
        snippets.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        snippets.BorderStyle = BorderStyle.None;
        snippets.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        snippets.ColumnHeadersHeight = 31;
        snippets.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        snippets.EnableHeadersVisualStyles = false;
        snippets.MultiSelect = false;
        snippets.RowHeadersVisible = false;
        snippets.RowTemplate.Height = 33;
        snippets.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        snippets.DefaultCellStyle.Padding = new Padding(6, 0, 0, 0);
        snippets.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Abbreviation", FillWeight = 30, MaxInputLength = 64, SortMode = DataGridViewColumnSortMode.NotSortable });
        snippets.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Replacement text", FillWeight = 70, MaxInputLength = 4096, SortMode = DataGridViewColumnSortMode.NotSortable });
        foreach (var snippet in settings.Snippets) snippets.Rows.Add(snippet.Trigger, snippet.Text);
        snippets.SelectionChanged += delegate { remove.Enabled = snippets.CurrentRow != null; };
        remove.Enabled = snippets.Rows.Count != 0;
        BuildTextMenu();
        snippets.EditingControlShowing += delegate(object sender, DataGridViewEditingControlShowingEventArgs e)
        {
            e.Control.ContextMenuStrip = textMenu;
            theme.Apply(e.Control);
        };
        AddRow(rows, snippets, 202, 0);
        AddRow(page, card, 285, 0);
    }

    private TextBoxBase SnippetEditor { get { return snippets.EditingControl as TextBoxBase; } }

    private void BuildTextMenu()
    {
        var undo = AddTextAction("Undo", editor => editor.Undo());
        textMenu.Items.Add(new ToolStripSeparator());
        var cut = AddTextAction("Cut", editor => editor.Cut());
        var copy = AddTextAction("Copy", editor => editor.Copy());
        AddTextAction("Paste", editor => editor.Paste());
        var delete = AddTextAction("Delete", editor => editor.SelectedText = "");
        textMenu.Items.Add(new ToolStripSeparator());
        AddTextAction("Select all", editor => editor.SelectAll());
        textMenu.Opening += (sender, args) =>
        {
            var editor = SnippetEditor;
            if (editor == null)
            {
                args.Cancel = true;
                return;
            }
            undo.Enabled = editor.CanUndo;
            cut.Enabled = copy.Enabled = delete.Enabled = editor.SelectionLength > 0;
            theme.ApplyMenu(textMenu);
        };
    }

    private ToolStripItem AddTextAction(string text, Action<TextBoxBase> action)
    {
        return textMenu.Items.Add(text, null, delegate
        {
            var editor = SnippetEditor;
            if (editor != null) action(editor);
        });
    }

    private void BuildAppearance(TableLayoutPanel page)
    {
        TableLayoutPanel rows;
        var card = Card(out rows);
        AddRow(rows, Heading("Color theme"), 28, 0);
        AddRow(rows, TextLabel("System matches the Windows app color setting.", true), 32, 5);
        var choices = Table(3);
        for (int i = 0; i < 3; i++) choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        AddTheme(choices, AppearanceMode.System, "\uE7F4");
        AddTheme(choices, AppearanceMode.Light, "\uE706");
        AddTheme(choices, AppearanceMode.Dark, "\uE708");
        AddRow(rows, choices, 42, 0);
        AddRow(page, card, 146, 0);
    }

    private void BuildGeneral(TableLayoutPanel page, Settings settings)
    {
        launchInBackground.Checked = settings.LaunchInBackground;
        closeToTray.Checked = !settings.ExitOnClose;
        TableLayoutPanel rows;
        var card = Card(out rows);
        AddRow(rows, launchInBackground, 32, 0);
        AddRow(rows, TextLabel("Start with only the tray icon visible.", true, SettingsCheckBox.LabelInset), 26, 10);
        AddRow(rows, closeToTray, 32, 0);
        AddRow(rows, TextLabel("Keep KeyRewind running when you close this window.", true, SettingsCheckBox.LabelInset), 26, 0);
        AddRow(page, card, 160, 12);
        AddRow(page, TextLabel("Version " + Application.ProductVersion, true), 28, 0);
    }

    private void AddTheme(TableLayoutPanel choices, AppearanceMode mode, string glyph)
    {
        var button = Button(mode.ToString(), glyph, delegate
        {
            if (appearance == mode) return;
            appearance = mode;
            ApplyTheme();
            status.Text = "Save changes to keep this theme.";
        });
        int index = (int)mode;
        if (index != 2) button.Margin = new Padding(0, 0, 8, 0);
        themes[index] = button;
        choices.Controls.Add(button, index, 0);
    }

    private void ApplyTheme()
    {
        theme = SettingsTheme.Create(appearance);
        BackColor = theme.Background;
        theme.Apply(this);
        for (int i = 0; i < themes.Length; i++)
        {
            themes[i].Selected = (int)appearance == i;
            themes[i].AccessibleDescription = themes[i].Selected ? "Selected theme" : "";
        }
        if (IsHandleCreated) theme.ApplyCaption(Handle);
    }

    private void TrackScrollbars(Control control)
    {
        if (SettingsTheme.HasScrollbars(control))
            control.HandleCreated += (sender, args) => theme.ApplyScrollbars(control);
        foreach (Control child in control.Controls) TrackScrollbars(child);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SettingsTheme.DisableWindowTransitions(Handle);
        if (theme != null) theme.ApplyCaption(Handle);
    }

    private void UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsHandleCreated && !IsDisposed && !Disposing)
        {
            try { BeginInvoke((MethodInvoker)ApplyTheme); }
            catch (InvalidOperationException) { }
        }
    }

    private void SaveChanges()
    {
        StopRecording();
        if (!snippets.EndEdit()) return;
        var settings = new Settings
        {
            Convert = convert, Expand = expand, Appearance = appearance,
            ShowAutoswitchPopups = autoswitchPopups.Checked,
            LaunchInBackground = launchInBackground.Checked, ExitOnClose = !closeToTray.Checked,
            LayoutA = installed.Length > 2 ? SelectedLayout(layoutA) : original.LayoutA,
            LayoutB = installed.Length > 2 ? SelectedLayout(layoutB) : original.LayoutB,
            Snippets = snippets.Rows.Cast<DataGridViewRow>().Select(row => new Snippet
            {
                Trigger = (string)row.Cells[0].Value, Text = (string)row.Cells[1].Value
            }).ToList()
        };
        if (installed.Length > 2 && (settings.LayoutA != 0 || settings.LayoutB != 0) &&
            (settings.LayoutA == 0 || settings.LayoutB == 0 || settings.LayoutA == settings.LayoutB))
        {
            ShowPage(0);
            status.Text = "Choose two different layouts or clear both selections.";
            return;
        }
        try
        {
            RememberPlacement(settings);
            apply(settings);
            status.Text = "Changes saved.";
        }
        catch (Exception error)
        {
            if (!(error is InvalidDataException || error is IOException ||
                error is UnauthorizedAccessException || error is SerializationException)) throw;
            status.Text = error.Message;
        }
    }

    private static SettingsPanel Card(out TableLayoutPanel rows)
    {
        var panel = new SettingsPanel { IsCard = true, Padding = new Padding(17) };
        rows = Table(1);
        rows.Dock = DockStyle.Fill;
        panel.Controls.Add(rows);
        return panel;
    }

    private static TableLayoutPanel Table(int columns)
    {
        return new TableLayoutPanel
        {
            ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, Margin = Padding.Empty
        };
    }

    private static void AddRow(TableLayoutPanel table, Control item, int height, int gap)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, height + gap));
        item.Dock = DockStyle.Fill;
        item.Margin = new Padding(0, 0, 0, gap);
        table.Controls.Add(item, 0, row);
    }

    private Label Heading(string text)
    {
        var label = TextLabel(text, false);
        label.Font = headingFont;
        return label;
    }

    private static Label TextLabel(string text, bool muted, int indent = 0)
    {
        return new Label
        {
            Text = text, Tag = muted ? "muted" : null, TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(indent, 0, 0, 0),
            UseMnemonic = false, AutoEllipsis = true
        };
    }

    private static SettingsButton Button(string text, string glyph, EventHandler click)
    {
        var button = new SettingsButton { Text = text, Glyph = glyph, Dock = DockStyle.Fill, Margin = Padding.Empty };
        if (click != null) button.Click += click;
        return button;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        StopRecording();
        base.OnDeactivate(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopRecording();
            SystemEvents.UserPreferenceChanged -= UserPreferenceChanged;
            if (layoutA.Parent == null) layoutA.Dispose();
            if (layoutB.Parent == null) layoutB.Dispose();
            shortcutTips.Dispose();
            textMenu.Dispose();
        }
        base.Dispose(disposing);
        if (disposing)
        {
            titleFont.Dispose();
            headingFont.Dispose();
            bodyFont.Dispose();
        }
    }
}
