using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

internal static class SmokeTests
{
    [STAThread]
    static void Main()
    {
        var settings = new Settings();
        const string replacement = "test@example.test";
        settings.Snippets.Add(new Snippet { Trigger = "n@.", Text = replacement });
        settings.Validate();
        var snippets = settings.Expansions;
        var buffer = new SnippetBuffer();
        string text;
        int erase;
        buffer.Type("n@.");
        Check(!buffer.Space(100, false, snippets, out text, out erase), "First space must pass through");
        Check(buffer.Space(200, false, snippets, out text, out erase) && text == replacement && erase == 4, "Double space expands the exact abbreviation");
        buffer.Type("an@.");
        buffer.Space(100, false, snippets, out text, out erase);
        Check(!buffer.Space(200, false, snippets, out text, out erase), "Abbreviations must match a whole token");
        buffer.Clear();
        buffer.Type("n@.");
        buffer.Space(100, false, snippets, out text, out erase);
        Check(!buffer.Space(800, false, snippets, out text, out erase), "Slow spaces must remain spaces");
        buffer.Clear();
        buffer.Type("n@.");
        buffer.Space(100, false, snippets, out text, out erase);
        Check(!buffer.Space(200, true, snippets, out text, out erase), "Holding Space must leave ordinary spaces");
        buffer.Clear();
        buffer.Type("n@.z");
        buffer.Backspace();
        Check(buffer.Find(snippets, 0, out text, out erase) && text == replacement && erase == 3, "Custom shortcut expands after a corrected typo");
        buffer.Type("n@.");
        buffer.Clear();
        Check(!buffer.Find(snippets, 0, out text, out erase), "Clearing context discards a pending abbreviation");
        settings.Expand = settings.Convert;
        try { settings.Validate(); throw new Exception("Duplicate shortcuts accepted"); }
        catch (InvalidDataException) { }
        Check(Marshal.SizeOf(typeof(WindowsInput.Input)) == (IntPtr.Size == 8 ? 40 : 28), "SendInput ABI must match Windows");

        CheckDefaultSnippets();
        CheckLayoutPair();
        CheckAppearance();
        CheckAutoswitchPopups();
        CheckWindowPreferences();
        CheckWindowPlacement();
        CheckSavedGamingMode();
        CheckGamingTrayIcon();
        CheckInputLifecycle();
        CheckNativeEditSelection();
        var installed = LayoutConverter.Installed();
        var english = installed.FirstOrDefault(item => item.Culture.Name == "en-US");
        var ukrainian = installed.FirstOrDefault(item => item.Culture.Name == "uk-UA");
        if (english != null && ukrainian != null)
        {
            var a = english.Handle;
            var b = ukrainian.Handle;
            IntPtr destination;
            Check(LayoutConverter.Convert("Ghbdtn!\r\n♡", b, a, out destination) == "Привет!\r\n♡" && destination == b,
                "Physical keys preserve case, whitespace, and unsupported characters");
            Check(LayoutConverter.Convert("руддщ", b, a, out destination) == "hello" && destination == a,
                "Ukrainian text converts back to English and selects English");
            Check(LayoutConverter.Convert("ghbdsn", a, b, out destination) == "привіт" && destination == b,
                "English keys convert to Ukrainian and select Ukrainian");
            Check(LayoutConverter.Convert("фффf", a, b, out destination) == "aaaf" && destination == a,
                "Repeated letters must each count towards the detected source layout");
            Check(LayoutConverter.Convert("Ghbdtn! Ghbdtn!\r\n\u2661🙂", a, b, out destination) == "Привет! Привет!\r\n\u2661🙂" && destination == b,
                "Repeated characters and unsupported Unicode must convert without losing whitespace or punctuation");
        }
        else Console.WriteLine("English–Ukrainian mapping test skipped because those layouts are absent.");
        Console.WriteLine("Smoke tests passed.");
    }

    private static void CheckDefaultSnippets()
    {
        string path = Path.Combine(Path.GetTempPath(), "KeyRewind-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var fresh = Settings.Load(path);
            Check(fresh.Snippets.Count == 10 && fresh.Expand == null && fresh.Expansions["krthanks"] == "Thanks for your help!",
                "Fresh settings should have ten example snippets and use Double Space");

            var serializer = new DataContractJsonSerializer(typeof(Settings));
            var saved = new Settings();
            saved.Snippets.Add(new Snippet { Trigger = "krthanks", Text = "Custom thanks" });
            using (var stream = File.Create(path)) serializer.WriteObject(stream, saved);
            var existing = Settings.Load(path);
            Check(existing.Snippets.Count == 1 && existing.Expansions["krthanks"] == "Custom thanks",
                "Existing settings should use the saved snippets");

            saved.Snippets.Clear();
            using (var stream = File.Create(path)) serializer.WriteObject(stream, saved);
            Check(Settings.Load(path).Snippets.Count == 0, "A saved empty list should stay empty");
        }
        finally { File.Delete(path); }
    }

    private static void CheckLayoutPair()
    {
        var us = new IntPtr(0x04090409);
        var ukrainian = new IntPtr(unchecked((int)0xF0C00422));
        var german = new IntPtr(0x04070407);
        var layouts = new[] { us, ukrainian, german };
        var settings = new Settings();
        IntPtr first, second;
        Check(!LayoutConverter.GetPair(layouts, settings, out first, out second),
            "Three layouts require a saved pair");
        settings.LayoutA = LayoutConverter.LayoutId(ukrainian);
        settings.LayoutB = LayoutConverter.LayoutId(german);
        Check(LayoutConverter.GetPair(layouts.Reverse(), settings, out first, out second) && first == ukrainian && second == german,
            "The saved pair works when Windows reorders the layouts");
        Check(LayoutConverter.OtherLayout(first, second, first) == second && LayoutConverter.OtherLayout(first, second, second) == first,
            "Either selected layout can switch to the other");
        Check(LayoutConverter.OtherLayout(first, second, us) == IntPtr.Zero,
            "An active layout outside the pair cannot switch or convert");
        Check(LayoutConverter.GetPair(layouts.Take(2), settings, out first, out second) && first == us && second == ukrainian,
            "Two installed layouts are selected automatically even when a saved layout is gone");
        settings.LayoutB = 0x04190419;
        Check(!LayoutConverter.GetPair(layouts, settings, out first, out second),
            "A missing saved layout requires a new choice when three layouts are installed");
    }

    private static void CheckAppearance()
    {
        var serializer = new DataContractJsonSerializer(typeof(Settings));
        using (var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Snippets\":[]}")))
        {
            var settings = (Settings)serializer.ReadObject(legacy);
            Check(settings.Appearance == AppearanceMode.System, "Settings without a theme should follow Windows");
        }
        foreach (AppearanceMode appearance in Enum.GetValues(typeof(AppearanceMode)))
        {
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, new Settings { Appearance = appearance });
                stream.Position = 0;
                Check(((Settings)serializer.ReadObject(stream)).Appearance == appearance, "The selected theme should survive saving");
            }
        }
    }

    private static void CheckWindowPreferences()
    {
        var defaults = new Settings();
        Check(!defaults.LaunchInBackground && !defaults.ExitOnClose,
            "On first launch Settings should open and close to the tray by default");
        var serializer = new DataContractJsonSerializer(typeof(Settings));
        using (var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Snippets\":[]}")))
        {
            var settings = (Settings)serializer.ReadObject(legacy);
            Check(!settings.LaunchInBackground && !settings.ExitOnClose,
                "Existing settings should open the window on launch and close it to the tray");
            Check(!settings.CloseToTrayNoticeShown, "Existing settings should allow the first close-to-tray notice");
        }
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, new Settings { LaunchInBackground = true, ExitOnClose = true, CloseToTrayNoticeShown = true });
            stream.Position = 0;
            var saved = (Settings)serializer.ReadObject(stream);
            Check(saved.LaunchInBackground && saved.ExitOnClose, "Window preferences should survive saving and reloading");
            Check(saved.CloseToTrayNoticeShown, "The close-to-tray notice should stay dismissed after reloading");
        }
    }

    private static void CheckAutoswitchPopups()
    {
        Check(new Settings().ShowAutoswitchPopups, "New settings should show autoswitch popups");
        var serializer = new DataContractJsonSerializer(typeof(Settings));
        using (var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Snippets\":[]}")))
            Check(((Settings)serializer.ReadObject(legacy)).ShowAutoswitchPopups, "Existing settings should show autoswitch popups by default");

        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, new Settings { ShowAutoswitchPopups = false });
            stream.Position = 0;
            Check(!((Settings)serializer.ReadObject(stream)).ShowAutoswitchPopups, "Turning off autoswitch popups should survive saving");
        }
    }

    private static void CheckWindowPlacement()
    {
        var serializer = new DataContractJsonSerializer(typeof(Settings));
        using (var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Snippets\":[]}")))
        {
            var saved = (Settings)serializer.ReadObject(legacy);
            Check(saved.SettingsWindowBounds.IsEmpty && !saved.SettingsWindowMaximized,
                "Existing settings should open a centered, normal window");
        }

        var bounds = new Rectangle(-1400, 120, 900, 580);
        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, new Settings { SettingsWindowBounds = bounds, SettingsWindowMaximized = true });
            stream.Position = 0;
            var saved = (Settings)serializer.ReadObject(stream);
            Check(saved.SettingsWindowBounds == bounds && saved.SettingsWindowMaximized,
                "Window position, size, and maximized state should survive saving and reloading");
        }

        var area = new Rectangle(-1920, 0, 1920, 1040);
        var minimum = new Size(742, 516);
        var defaultSize = new Size(946, 664);
        Check(ModernSettingsForm.FitWindowBounds(bounds, area, minimum, defaultSize) == bounds,
            "Saved placement should be preserved on a monitor left of the primary one");
        Check(ModernSettingsForm.FitWindowBounds(new Rectangle(-80, 810, 900, 580), area, minimum, defaultSize) ==
            new Rectangle(-900, 460, 900, 580), "Partially off-screen windows should move inside the work area");
        Check(ModernSettingsForm.FitWindowBounds(bounds, new Rectangle(0, 0, 1280, 720), minimum, defaultSize) ==
            new Rectangle(190, 70, 900, 580), "A disconnected monitor should recenter the window on the available monitor");
        var smallArea = new Rectangle(100, 50, 640, 480);
        Check(ModernSettingsForm.FitWindowBounds(Rectangle.Empty, smallArea, minimum, defaultSize) == smallArea,
            "The default window should fit a screen smaller than its minimum size");
    }

    private static void CheckSavedGamingMode()
    {
        var serializer = new DataContractJsonSerializer(typeof(Settings));
        using (var legacy = new MemoryStream(Encoding.UTF8.GetBytes("{\"Snippets\":[]}")))
            Check(!((Settings)serializer.ReadObject(legacy)).GamingMode, "Existing settings should keep shortcuts enabled");

        using (var stream = new MemoryStream())
        {
            serializer.WriteObject(stream, new Settings { GamingMode = true });
            stream.Position = 0;
            var saved = (Settings)serializer.ReadObject(stream);
            Check(saved.GamingMode, "Gaming Mode should survive saving and reloading");
            using (var dispatcher = new Control())
            using (var engine = new KeyboardEngine(dispatcher, saved, message => { }))
            {
                Check(!engine.Enabled, "Saved Gaming Mode should start with shortcuts disabled");
                engine.Enable();
                Check(engine.Enabled, "Shortcuts can be enabled after starting in Gaming Mode");
            }
        }
    }

    private static void CheckGamingTrayIcon()
    {
        using (var stream = typeof(Program).Assembly.GetManifestResourceStream("KeyRewind.ico"))
        using (var windowIcon = new Icon(stream, 32, 32))
        foreach (int size in new[] { 16, 20, 24, 32, 40, 48 })
        {
            using (var trayIcon = new Icon(windowIcon, size, size))
            using (var decodedIcon = Icon.FromHandle(trayIcon.Handle))
            using (var original = decodedIcon.ToBitmap())
            using (var gamingIcon = TrayIcons.WithGamingBadge(trayIcon))
            using (var gaming = gamingIcon.ToBitmap())
            {
                Check(gaming.Size == original.Size, "The badge must preserve the tray icon size");
                int badgeStart = size - (int)Math.Ceiling(size * 6.0 / 16);
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        if (x >= badgeStart && y >= badgeStart) continue;
                        var before = original.GetPixel(x, y);
                        var after = gaming.GetPixel(x, y);
                        Check(before.ToArgb() == after.ToArgb() || (before.A == 0 && after.A == 0),
                            "Gaming Mode must preserve the artwork and transparency outside the badge");
                    }
                int badgeCenter = size - (int)Math.Ceiling(size * 3.0 / 16);
                Check(gaming.GetPixel(badgeCenter, badgeCenter).ToArgb() == TrayIcons.GamingViolet.ToArgb(),
                    "Gaming Mode must add a violet dot at the bottom right");
            }
        }
    }

    private static void CheckInputLifecycle()
    {
        var settings = new Settings();
        settings.Validate();
        using (var dispatcher = new Control())
        using (var engine = new KeyboardEngine(dispatcher, settings, message => { }))
        {
            Check(engine.Enabled, "Shortcuts should start enabled");
            engine.Recorded = hotkey => { };
            Check(engine.Disable() && !engine.Enabled, "Pausing should remove all input hooks");
            Check(engine.Recorded == null, "Pausing should cancel shortcut recording");
            engine.Enable();
            Check(engine.Enabled, "Resuming should reinstall the input hooks");
            Check(engine.Disable() && engine.Disable(), "Pausing again should release hooks and be safe to repeat");
        }
    }

    private static void CheckNativeEditSelection()
    {
        using (var editor = new TextBox { Multiline = true, Text = "before\r\nghbdsn after" })
        {
            var target = new InputTarget { Focus = editor.Handle };
            editor.Select(8, 6);
            var selection = Selection.Read(target, TargetKind.Editor);
            Check(selection != null && selection.CanReplaceDirectly && selection.Text == "ghbdsn",
                "A native edit should return only the selected text with the correct multiline offsets");
            Check(selection.CanTypeReplacement("привіт") && !selection.CanTypeReplacement("при\r\nвіт"),
                "Clipboard-free typing should allow ordinary text and reject line breaks");
            Check(!selection.ReplaceEdit(editor.Handle, "привіт", () => false), "A cancelled shortcut must leave the selection alone");
            editor.Select(0, 6);
            Check(!selection.ReplaceEdit(editor.Handle, "привіт", () => true), "A changed selection must not be overwritten");
            editor.Select(8, 6);
            Check(selection.ReplaceEdit(editor.Handle, "привіт", () => true) && editor.Text == "before\r\nпривіт after",
                "Native replacement should change only the selected text");
            editor.Undo();
            Check(editor.Text == "before\r\nghbdsn after", "Native replacement should be undoable");

            editor.ReadOnly = true;
            editor.Select(8, 6);
            selection = Selection.Read(target, TargetKind.Editor);
            Check(selection != null && selection.ReadOnly && selection.Text == "ghbdsn" && !selection.CanReplaceDirectly,
                "Read-only text should be available for copying only");
            Check(!selection.CanTypeReplacement("привіт"), "Clipboard-free typing must leave read-only text alone");
            editor.Select(8, 0);
            Check(Selection.Read(target, TargetKind.Editor).Text.Length == 0, "A caret alone is not a selection");
        }

        using (var password = new TextBox { PasswordChar = '*', Text = "ghbdsn" })
        {
            var target = new InputTarget { Focus = password.Handle };
            password.SelectAll();
            var selection = Selection.Read(target, TargetKind.Editor);
            Check(selection != null && selection.Text.Length == 0, "Password text must not be read for conversion");
        }
    }

    internal static void Check(bool pass, string message)
    {
        if (!pass) throw new Exception(message);
    }
}
