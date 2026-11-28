using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

internal sealed class KeyboardEngine : IDisposable
{
    private readonly Control dispatcher;
    private readonly IntPtr clipboardOwner;
    private readonly Action<string> notice;
    private readonly uint process = (uint)Process.GetCurrentProcess().Id;
    private readonly WindowsInput.Hook keyboardCallback, mouseCallback;
    private readonly WindowsInput.WinEvent focusCallback;
    private IntPtr keyboard, mouse, foreground, focus;
    private readonly bool[] down = new bool[256], swallowed = new bool[256];
    private readonly SnippetBuffer buffer = new SnippetBuffer();
    private InputTarget lastTarget;
    private IntPtr lastLayout;
    private bool capsLock;
    private volatile bool enabled;
    private long revision;
    private int busy;
    private Settings settings;

    internal Action<Hotkey> Recorded;
    internal bool Busy { get { return Volatile.Read(ref busy) != 0; } }
    internal bool Enabled { get { return enabled; } }

    internal KeyboardEngine(Control dispatcher, Settings settings, Action<string> notice)
    {
        this.dispatcher = dispatcher;
        clipboardOwner = dispatcher.Handle;
        this.settings = settings;
        this.notice = notice;

        keyboardCallback = Keyboard;
        mouseCallback = Mouse;
        focusCallback = FocusChanged;
        if (!settings.GamingMode) Enable();
    }

    internal void Enable()
    {
        if (enabled) return;
        if (!Disable()) throw new Win32Exception("Windows could not release the existing input hooks.");

        capsLock = (WindowsInput.GetKeyState((int)Keys.CapsLock) & 1) != 0;
        for (int key = 1; key < down.Length; key++) down[key] = WindowsInput.Down(key);
        Array.Clear(swallowed, 0, swallowed.Length);

        IntPtr module = WindowsInput.GetModuleHandle(null);
        keyboard = WindowsInput.SetWindowsHookEx(13, keyboardCallback, module, 0);
        if (keyboard == IntPtr.Zero) throw new Win32Exception();
        mouse = WindowsInput.SetWindowsHookEx(14, mouseCallback, module, 0);
        if (mouse == IntPtr.Zero)
        {
            var error = new Win32Exception();
            Dispose();
            throw error;
        }
        foreground = WindowsInput.SetWinEventHook(3, 3, IntPtr.Zero, focusCallback, 0, 0, 0);
        focus = WindowsInput.SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, focusCallback, 0, 0, 0);
        if (foreground == IntPtr.Zero || focus == IntPtr.Zero)
        {
            Dispose();
            throw new Win32Exception("Windows could not start focus tracking.");
        }
        enabled = true;
    }

    internal void Apply(Settings updated)
    {
        settings = updated;
        Invalidate();
    }

    internal void Invalidate()
    {
        Interlocked.Increment(ref revision);
        buffer.Clear();
    }

    private void FocusChanged(IntPtr hook, uint eventType, IntPtr window, int obj, int child, uint thread, uint time)
    {
        if (enabled && (eventType == 3 || !lastTarget.SameAs(InputTarget.Read()))) Invalidate();
    }

    private IntPtr Mouse(int code, IntPtr message, IntPtr data)
    {
        if (enabled && code >= 0)
        {
            int kind = message.ToInt32();
            if (kind == 0x201 || kind == 0x204 || kind == 0x207 || kind == 0x20A || kind == 0x20B || kind == 0x20E)
                Invalidate();
        }
        return WindowsInput.CallNextHookEx(mouse, code, message, data);
    }

    private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
    {
        if (enabled && code >= 0)
        {
            var key = (WindowsInput.KeyEvent)Marshal.PtrToStructure(data, typeof(WindowsInput.KeyEvent));
            if (key.Extra != WindowsInput.Marker && key.Key > 0 && key.Key < 256 && HandleKey(key))
                return new IntPtr(1);
        }
        return WindowsInput.CallNextHookEx(keyboard, code, message, data);
    }

    private bool HandleKey(WindowsInput.KeyEvent stroke)
    {
        int key = (int)stroke.Key;
        bool release = (stroke.Flags & 0x80) != 0;
        bool repeat = down[key];
        down[key] = !release;
        if (release)
        {
            bool block = swallowed[key];
            swallowed[key] = false;
            return block;
        }
        if (swallowed[key]) return true;
        if (WindowsInput.ModifierKey(key))
        {
            if (!repeat) Interlocked.Increment(ref revision);
            return false;
        }
        if (key == (int)Keys.CapsLock)
        {
            if (!repeat) capsLock = !capsLock;
            return false;
        }

        long stamp = Interlocked.Increment(ref revision);
        var currentSettings = settings;
        if (currentSettings.Expansions.Count == 0 && Recorded == null && key != currentSettings.Convert.Key) return false;

        var target = InputTarget.Read();
        int modifiers = WindowsInput.Modifiers();
        bool altGr = (modifiers & WindowsInput.Control) != 0 && WindowsInput.Down(0xA5);

        if (target.Process == process)
        {
            var capture = Recorded;
            var hotkey = new Hotkey { Key = key, Modifiers = modifiers };
            if (capture == null || repeat || altGr || !hotkey.Valid()) return false;
            Recorded = null;
            return Consume(key, modifiers, () => capture(hotkey));
        }
        if (target.Window == IntPtr.Zero) return false;

        var layout = WindowsInput.GetKeyboardLayout(target.Thread);
        if (!target.SameAs(lastTarget) || layout != lastLayout) buffer.Clear();
        lastTarget = target;
        lastLayout = layout;

        if (!repeat && !altGr && currentSettings.Convert.Matches(key, modifiers))
        {
            buffer.Clear();
            if (Start(target, stamp, (kind, current) => Convert(target, kind, currentSettings, layout, current)))
                return Consume(key, modifiers, null);
            return false;
        }

        if (settings.Expansions.Count == 0) return false;
        string text;
        int erase;
        if (settings.Expand != null && !repeat && !altGr && settings.Expand.Matches(key, modifiers))
        {
            if (buffer.Find(settings.Expansions, 0, out text, out erase))
                return Expand(key, modifiers, target, stamp, text, erase, false);
            buffer.Clear();
            return false;
        }
        if (key == (int)Keys.Space && modifiers == 0)
        {
            if (settings.Expand == null && buffer.Space(stroke.Time, repeat, settings.Expansions, out text, out erase))
                return Expand(key, modifiers, target, stamp, text, erase, true);
            if (settings.Expand != null) buffer.Clear();
            return false;
        }
        if (key == (int)Keys.Back && modifiers == 0) { buffer.Backspace(); return false; }

        if ((modifiers & WindowsInput.Win) != 0 || !altGr && (modifiers & (WindowsInput.Control | WindowsInput.Alt)) != 0 ||
            key == 0xE5 || key == 0xE7)
        {
            buffer.Clear();
            return false;
        }
        var typed = new StringBuilder(8);
        int length = WindowsInput.ToUnicodeEx(stroke.Key, stroke.Scan & 255,
            WindowsInput.KeyState(modifiers, altGr, capsLock), typed, typed.Capacity, 4, layout);
        if (length <= 0) buffer.Clear();
        else
        {
            string value = typed.ToString(0, length);
            foreach (char character in value)
            {
                if (char.IsWhiteSpace(character) || char.IsControl(character)) { buffer.Clear(); return false; }
            }
            buffer.Type(value);
        }
        return false;
    }

    private bool Consume(int key, int modifiers, Action caught)
    {
        swallowed[key] = true;
        if ((modifiers & (WindowsInput.Alt | WindowsInput.Win)) != 0)
        {
            // An unused key prevents a swallowed chord from activating the menu or Start on release.
            var mask = new List<WindowsInput.Input>();
            WindowsInput.Chord(mask, (Keys)0xE8, 0);
            Send(mask);
        }
        if (caught != null) dispatcher.BeginInvoke(caught);
        return true;
    }

    private bool Expand(int key, int modifiers, InputTarget target, long stamp, string text, int erase, bool secondSpace)
    {
        if (modifiers == 0 && target.Kind() == TargetKind.Editor)
        {
            if (!InsertSnippet(text, erase)) return false;
            return Consume(key, modifiers, null);
        }
        if (!Start(target, stamp, (kind, current) =>
        {
            if (kind == TargetKind.Editor) InsertSnippet(text, erase);
            else if (!PasteSnippet(kind, text, erase, current) && secondSpace && current())
            {
                var input = new List<WindowsInput.Input>();
                WindowsInput.Chord(input, Keys.Space, 0);
                Send(input);
            }
        }, true)) return false;
        return Consume(key, modifiers, null);
    }

    private bool Start(InputTarget target, long stamp, Action<TargetKind, Func<bool>> action, bool waitForModifiers = false)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Func<bool> current = () => enabled && Interlocked.Read(ref revision) == stamp && target.SameAs(InputTarget.Read());
            try
            {
                if (current() && (!waitForModifiers || WaitForModifiers(current))) action(target.Kind(), current);
            }
            catch (Exception error)
            {
                if (error is OutOfMemoryException || error is StackOverflowException) throw;
                Notify("Windows could not complete the shortcut.");
            }
            finally { Interlocked.Exchange(ref busy, 0); }
        });
        return true;
    }

    private static bool WaitForModifiers(Func<bool> current)
    {
        if (WindowsInput.Modifiers() == 0) return current();
        var timer = Stopwatch.StartNew();
        while (current() && timer.ElapsedMilliseconds < 1000)
        {
            if (WindowsInput.Modifiers() == 0) return current();
            Thread.Sleep(4);
        }
        return false;
    }

    private void Convert(InputTarget target, TargetKind kind, Settings currentSettings, IntPtr layout, Func<bool> focused)
    {
        var installed = LayoutConverter.Installed();
        IntPtr first, second;
        if (!LayoutConverter.GetPair(installed.Select(item => item.Handle), currentSettings, out first, out second))
        {
            Notify(installed.Length < 2
                ? "Install two Windows keyboard layouts to convert a selection."
                : "Choose two autoswitch layouts in Settings to convert a selection.");
            return;
        }

        IntPtr other = LayoutConverter.OtherLayout(first, second, layout);
        if (other == IntPtr.Zero)
        {
            Notify("Activate one of your two autoswitch layouts to convert a selection.");
            return;
        }
        Func<bool> current = () => focused() && WindowsInput.GetKeyboardLayout(target.Thread) == layout;
        if (!current()) return;

        // Read and convert the selection while the shortcut keys are being released.
        var selection = Selection.Read(target, kind);
        if (!current() || selection != null && selection.Text.Length == 0) return;
        if (selection == null && kind == TargetKind.Editor && Selection.NeedsConfirmedSelection(target))
        {
            Notify("This editor needs to expose its selection through accessibility.");
            return;
        }

        ClipboardSession clipboard = null;
        try
        {
            string selected;
            if (selection == null)
            {
                if (!WaitForModifiers(current)) return;
                clipboard = CaptureClipboard(current);
                if (clipboard == null) return;
                selected = CopySelection(clipboard, target, kind, current);
            }
            else selected = selection.Text;
            if (string.IsNullOrEmpty(selected) || !current()) return;

            IntPtr destination;
            string converted = LayoutConverter.Convert(selected, layout, other, out destination);
            if (selected == converted || !WaitForModifiers(current)) return;

            bool replace = kind == TargetKind.Editor && (selection == null || !selection.ReadOnly);
            string copied = replace ? null : "Converted selection copied to the clipboard.";
            Action finish = () => CompleteConversion(target, layout, destination, currentSettings.ShowAutoswitchPopups, copied, installed, focused);
            if (replace && selection != null && selection.CanReplaceDirectly && converted.Length == selected.Length)
            {
                if (selection.ReplaceEdit(target.Focus, converted, current)) finish();
                return;
            }

            if (clipboard == null && !replace)
            {
                if (ClipboardSession.WriteText(clipboardOwner, converted, current)) finish();
                else NotifyClipboardChanged(current);
                return;
            }

            if (clipboard == null)
            {
                bool clipboardBusy;
                clipboard = ClipboardSession.Capture(clipboardOwner, current, out clipboardBusy);
                if (clipboard == null)
                {
                    if (TryTypeReplacement(target, selection, converted, current)) finish();
                    else NotifyClipboardUnavailable(clipboardBusy, current);
                    return;
                }
            }

            if (!SetClipboardText(clipboard, converted, replace, current)) return;
            if (!replace) finish();
            else if (current() && clipboard.IsCurrent()) Paste(0, kind, finish);
        }
        finally { if (clipboard != null) clipboard.Dispose(); }
    }

    private bool TryTypeReplacement(InputTarget target, Selection selection, string converted, Func<bool> current)
    {
        if (!selection.CanTypeReplacement(converted) || !current()) return false;
        var confirmed = Selection.Read(target, TargetKind.Editor);
        if (confirmed == null || confirmed.Text != selection.Text || !confirmed.CanTypeReplacement(converted)) return false;
        var input = new List<WindowsInput.Input>(converted.Length * 2);
        WindowsInput.Text(input, converted);
        return current() && Send(input);
    }

    private void CompleteConversion(InputTarget target, IntPtr original, IntPtr destination, bool showPopup, string copied,
        InputLanguage[] installed, Func<bool> focused)
    {
        string layoutSwitch = null;
        string layoutName = null;
        if (original != destination)
        {
            bool switched = WindowsInput.SwitchKeyboardLayout(target, original, destination, focused);
            if (showPopup && focused())
            {
                if (switched)
                {
                    var language = installed.FirstOrDefault(item => item.Handle == destination);
                    layoutSwitch = language == null ? "Keyboard layout switched." : "Switched keyboard layout to {0}.";
                    if (language != null) layoutName = language.LayoutName;
                }
                else if (WindowsInput.GetKeyboardLayout(target.Thread) == original)
                    layoutSwitch = "Windows could not switch to the converted layout.";
            }
        }
        if (copied != null || layoutSwitch != null) Notify(copied, layoutSwitch, layoutName);
    }

    private string CopySelection(ClipboardSession clipboard, InputTarget target, TargetKind kind, Func<bool> current)
    {
        if (!current() || !clipboard.IsCurrent()) return null;
        var input = new List<WindowsInput.Input>();
        WindowsInput.Chord(input, kind == TargetKind.Editor ? Keys.C : Keys.Insert, WindowsInput.Control);
        if (!Send(input)) return null;
        var timer = Stopwatch.StartNew();
        // Take ownership of this copy even if a focus change cancels the replacement.
        while (timer.ElapsedMilliseconds < 550)
        {
            string text = clipboard.ReadCopy(target, () => true);
            if (text != null) return text;
            Thread.Sleep(12);
        }
        return null;
    }

    private bool InsertSnippet(string text, int erase)
    {
        var input = WindowsInput.Backspaces(erase);
        WindowsInput.Text(input, text);
        return Send(input);
    }

    private bool PasteSnippet(TargetKind kind, string text, int erase, Func<bool> current)
    {
        using (var clipboard = CaptureClipboard(current))
        {
            if (clipboard == null || !SetClipboardText(clipboard, text, true, current)) return false;
            return current() && clipboard.IsCurrent() && Paste(erase, kind);
        }
    }

    private ClipboardSession CaptureClipboard(Func<bool> current)
    {
        bool busy;
        var clipboard = ClipboardSession.Capture(clipboardOwner, current, out busy);
        if (clipboard == null) NotifyClipboardUnavailable(busy, current);
        return clipboard;
    }

    private void NotifyClipboardUnavailable(bool busy, Func<bool> current)
    {
        if (current()) Notify(busy ? "Another app is using the clipboard. Try the shortcut again." :
            "KeyRewind could not preserve the current clipboard contents. Copy plain text and try again.");
    }

    private void NotifyClipboardChanged(Func<bool> current)
    {
        if (current()) Notify("The clipboard changed or became unavailable. Try the shortcut again.");
    }

    private bool SetClipboardText(ClipboardSession clipboard, string text, bool temporary, Func<bool> current)
    {
        if (clipboard.SetText(text, temporary, current)) return true;
        NotifyClipboardChanged(current);
        return false;
    }

    private bool Paste(int erase, TargetKind kind, Action onQueued = null)
    {
        var input = WindowsInput.Backspaces(erase);
        if (kind == TargetKind.Console) WindowsInput.Chord(input, Keys.Insert, WindowsInput.Shift);
        else WindowsInput.Chord(input, Keys.V, WindowsInput.Control | (kind == TargetKind.Terminal ? WindowsInput.Shift : 0));
        if (!Send(input)) return false;
        if (onQueued != null) onQueued();
        Thread.Sleep(300);
        return true;
    }

    private void Notify(string message, string layoutSwitch = null, string layoutName = null)
    {
        if (enabled && !dispatcher.IsDisposed)
            dispatcher.BeginInvoke((Action)(() =>
            {
                if (!enabled) return;
                string text = message == null ? null : UiText.Get(message);
                if (settings.ShowAutoswitchPopups && layoutSwitch != null)
                {
                    string switched = UiText.Get(layoutSwitch, layoutName);
                    text = text == null ? switched : text + " " + switched;
                }
                if (text != null) notice(text);
            }));
    }

    private bool Send(List<WindowsInput.Input> input)
    {
        return enabled && WindowsInput.Send(input);
    }

    internal bool Disable()
    {
        enabled = false;
        Recorded = null;
        Invalidate();
        RemoveInputHook(ref keyboard);
        RemoveInputHook(ref mouse);
        if (foreground != IntPtr.Zero && WindowsInput.UnhookWinEvent(foreground)) foreground = IntPtr.Zero;
        if (focus != IntPtr.Zero && WindowsInput.UnhookWinEvent(focus)) focus = IntPtr.Zero;
        return keyboard == IntPtr.Zero && mouse == IntPtr.Zero && foreground == IntPtr.Zero && focus == IntPtr.Zero;
    }

    private static void RemoveInputHook(ref IntPtr hook)
    {
        const int invalidHookHandle = 1404;
        if (hook != IntPtr.Zero && (WindowsInput.UnhookWindowsHookEx(hook) || Marshal.GetLastWin32Error() == invalidHookHandle))
            hook = IntPtr.Zero;
    }

    public void Dispose() { Disable(); }
}
