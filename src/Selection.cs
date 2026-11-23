using System;
using System.Diagnostics;
using System.Text;
using System.Windows.Automation;

internal sealed class Selection
{
    private const int Limit = 1024 * 1024;
    private const int PasswordStyle = 0x0020, ReadOnlyStyle = 0x0800, NumberStyle = 0x2000;
    internal readonly string Text;
    internal readonly bool ReadOnly;
    private readonly bool writable;
    private readonly int editStart, editEnd;
    internal bool CanReplaceDirectly { get { return editStart >= 0 && writable; } }

    private Selection(string text, bool? readOnly, int editStart = -1, int editEnd = -1)
    {
        Text = text;
        ReadOnly = readOnly == true;
        writable = readOnly == false;
        this.editStart = editStart;
        this.editEnd = editEnd;
    }

    internal static Selection Read(InputTarget target, TargetKind kind)
    {
        if (kind == TargetKind.Console) return null;
        string name = WindowsInput.WindowClass(target.Focus);
        if (name.Equals("Edit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase))
            return ReadEdit(target.Focus);
        if (name.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("WindowsForms10.RichEdit", StringComparison.OrdinalIgnoreCase))
            return ReadRichEdit(target, kind);
        if (name == "Scintilla")
        {
            UIntPtr start, end;
            if (WindowsInput.SendMessageTimeout(target.Focus, 2143, UIntPtr.Zero, IntPtr.Zero, 2, 100, out start) != IntPtr.Zero &&
                WindowsInput.SendMessageTimeout(target.Focus, 2145, UIntPtr.Zero, IntPtr.Zero, 2, 100, out end) != IntPtr.Zero)
                return start == end ? new Selection("", false) : null;
        }

        return ReadAccessible(target, kind);
    }

    private static Selection ReadAccessible(InputTarget target, TargetKind kind)
    {
        try
        {
            var request = new CacheRequest();
            request.Add(AutomationElement.ProcessIdProperty);
            request.Add(AutomationElement.IsPasswordProperty);
            request.Add(TextPattern.Pattern);
            AutomationElement element;
            using (request.Activate()) element = AutomationElement.FocusedElement;
            if (element == null || kind == TargetKind.Editor && element.Cached.ProcessId != target.Process) return null;
            if (element.Cached.IsPassword) return new Selection("", true);
            object pattern;
            if (!element.TryGetCachedPattern(TextPattern.Pattern, out pattern)) return null;
            var ranges = ((TextPattern)pattern).GetSelection();
            if (ranges == null) return null;
            if (ranges.Length == 0) return new Selection("", false);

            // Replacing a disjoint selection with one paste has application-specific behavior.
            if (ranges.Length != 1) return new Selection("", false);
            string text = ranges[0].GetText(Limit);
            if (text.Length == Limit) return new Selection("", false);
            return new Selection(text, ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) as bool?);
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
        catch (ElementNotAvailableException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static Selection ReadRichEdit(InputTarget target, TargetKind kind)
    {
        IntPtr window = target.Focus;
        int style = WindowsInput.GetWindowLong(window, -16);
        if (IsPassword(window, style)) return new Selection("", true);

        int start, end;
        if (!WindowsInput.IsWindowUnicode(window) || !WindowsInput.TryGetEditSelection(window, out start, out end))
            return ReadAccessible(target, kind);
        bool readOnly = (style & ReadOnlyStyle) != 0;
        if (start == end) return new Selection("", readOnly);

        // Use accessibility for the text because Rich Edit offsets can count paragraph breaks differently than WM_GETTEXT.
        var selection = ReadAccessible(target, kind);
        if (selection == null || selection.Text.Length == 0) return selection;
        if (readOnly) return new Selection(selection.Text, true);
        int currentStart, currentEnd;
        if (!selection.writable || start < 0 || end < start || (style & NumberStyle) != 0 ||
            !WindowsInput.TryGetEditSelection(window, out currentStart, out currentEnd) || start != currentStart || end != currentEnd)
            return selection;
        return new Selection(selection.Text, false, start, end);
    }

    private static Selection ReadEdit(IntPtr window)
    {
        int style = WindowsInput.GetWindowLong(window, -16);
        if (IsPassword(window, style)) return new Selection("", true);

        int start, end;
        if (!WindowsInput.TryGetEditSelection(window, out start, out end)) return null;
        bool readOnly = (style & ReadOnlyStyle) != 0;
        if (start == end) return new Selection("", readOnly);
        if (start < 0 || end < start || end >= Limit || !WindowsInput.IsWindowUnicode(window)) return null;

        var text = new StringBuilder(end + 1);
        UIntPtr copied;
        const uint getText = 0x000D;
        if (WindowsInput.SendEditText(window, getText, (UIntPtr)text.Capacity, text, 2, 100, out copied) == IntPtr.Zero ||
            copied.ToUInt64() < (ulong)end) return null;
        return new Selection(text.ToString(start, end - start), readOnly, (style & NumberStyle) == 0 ? start : -1, end);
    }

    private static bool IsPassword(IntPtr window, int style)
    {
        UIntPtr passwordCharacter;
        return (style & PasswordStyle) != 0 ||
            WindowsInput.SendMessageTimeout(window, 0xD2, UIntPtr.Zero, IntPtr.Zero, 2, 100, out passwordCharacter) == IntPtr.Zero ||
            passwordCharacter != UIntPtr.Zero;
    }

    internal bool ReplaceEdit(IntPtr window, string replacement, Func<bool> current)
    {
        if (!CanReplaceDirectly || replacement.Length != Text.Length || !current()) return false;
        int style = WindowsInput.GetWindowLong(window, -16);
        if ((style & (ReadOnlyStyle | NumberStyle)) != 0 || IsPassword(window, style)) return false;
        int start, end;
        if (!WindowsInput.TryGetEditSelection(window, out start, out end) ||
            start != editStart || end != editEnd || !current()) return false;

        const uint replaceSelection = 0x00C2;
        UIntPtr result;
        return WindowsInput.SendEditText(window, replaceSelection, (UIntPtr)1, new StringBuilder(replacement), 2, 100, out result) != IntPtr.Zero;
    }

    internal bool CanTypeReplacement(string replacement)
    {
        return writable && IsShortSingleLine(Text) && IsShortSingleLine(replacement);
    }

    private static bool IsShortSingleLine(string text)
    {
        if (text.Length == 0 || text.Length > 1024) return false;
        foreach (char character in text)
            if (char.IsControl(character) || character == '\u2028' || character == '\u2029') return false;
        return true;
    }

    internal static bool NeedsConfirmedSelection(InputTarget target)
    {
        try
        {
            using (var process = Process.GetProcessById((int)target.Process))
            {
                string name = process.ProcessName.ToLowerInvariant();
                return name == "code" || name == "code - insiders" || name == "vscodium" || name == "devenv";
            }
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }
}
