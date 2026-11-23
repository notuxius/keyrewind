using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;

internal static class LayoutConverter
{
    private struct LayoutKeys
    {
        internal short Active, Other;
    }

    internal static InputLanguage[] Installed()
    {
        return InputLanguage.InstalledInputLanguages.Cast<InputLanguage>().ToArray();
    }

    internal static bool GetPair(IEnumerable<IntPtr> installed, Settings settings, out IntPtr first, out IntPtr second)
    {
        IntPtr[] layouts = installed.Distinct().ToArray();
        if (layouts.Length == 2)
        {
            first = layouts[0];
            second = layouts[1];
            return true;
        }

        first = Array.Find(layouts, layout => LayoutId(layout) == settings.LayoutA);
        second = Array.Find(layouts, layout => LayoutId(layout) == settings.LayoutB);
        return first != IntPtr.Zero && second != IntPtr.Zero && first != second;
    }

    internal static int LayoutId(IntPtr layout) { return unchecked((int)layout.ToInt64()); }

    internal static IntPtr OtherLayout(IntPtr first, IntPtr second, IntPtr active)
    {
        if (active == first) return second;
        return active == second ? first : IntPtr.Zero;
    }

    internal static string Convert(string text, IntPtr active, IntPtr other, out IntPtr destination)
    {
        var letterKeys = new Dictionary<char, LayoutKeys>();
        int direction = 0;
        foreach (char character in text)
        {
            if (!char.IsLetter(character)) continue;
            LayoutKeys keys;
            if (!letterKeys.TryGetValue(character, out keys))
            {
                keys = new LayoutKeys { Active = WindowsInput.VkKeyScanEx(character, active), Other = WindowsInput.VkKeyScanEx(character, other) };
                letterKeys.Add(character, keys);
            }
            bool inActive = keys.Active != -1;
            bool inOther = keys.Other != -1;
            if (inActive != inOther) direction += inActive ? 1 : -1;
        }

        IntPtr source = direction < 0 ? other : active;
        destination = direction < 0 ? active : other;
        var output = new StringBuilder(text.Length);
        var buffer = new StringBuilder(8);
        var converted = new Dictionary<char, string>();
        byte[][] states = {
            WindowsInput.KeyState(0, false, false), WindowsInput.KeyState(WindowsInput.Shift, false, false),
            WindowsInput.KeyState(0, true, false), WindowsInput.KeyState(WindowsInput.Shift, true, false)
        };

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                output.Append(character);
                continue;
            }
            string translated;
            if (!converted.TryGetValue(character, out translated))
            {
                LayoutKeys keys;
                short chord = letterKeys.TryGetValue(character, out keys) ? (direction < 0 ? keys.Other : keys.Active) :
                    WindowsInput.VkKeyScanEx(character, source);
                translated = Translate(chord, source, destination, states, buffer);
                converted.Add(character, translated);
            }
            if (translated == null) output.Append(character);
            else output.Append(translated);
        }
        return output.ToString();
    }

    private static string Translate(short chord, IntPtr source, IntPtr destination, byte[][] states, StringBuilder buffer)
    {
        int modifiers = chord >> 8;
        if (chord == -1 || modifiers != 0 && modifiers != 1 && modifiers != 6 && modifiers != 7) return null;
        uint scan = WindowsInput.MapVirtualKeyEx((uint)(chord & 255), 4, source);
        uint key = WindowsInput.MapVirtualKeyEx(scan, 3, destination);
        if (key == 0) return null;
        int length = WindowsInput.ToUnicodeEx(key, scan & 255,
            states[(modifiers & 1) + (modifiers >= 6 ? 2 : 0)], buffer, buffer.Capacity, 4, destination);
        return length > 0 ? buffer.ToString(0, length) : null;
    }
}
