using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal enum TargetKind { Editor, Terminal, Console }

internal struct InputTarget
{
    public IntPtr Window;
    public IntPtr Focus;
    public uint Thread;
    public uint Process;

    public static InputTarget Read()
    {
        var target = new InputTarget { Window = WindowsInput.GetForegroundWindow() };
        target.Thread = WindowsInput.GetWindowThreadProcessId(target.Window, out target.Process);
        var info = new WindowsInput.GuiThreadInfo { Size = Marshal.SizeOf(typeof(WindowsInput.GuiThreadInfo)) };
        target.Focus = WindowsInput.GetGUIThreadInfo(target.Thread, ref info) ? info.Focus : IntPtr.Zero;
        return target;
    }

    public bool SameAs(InputTarget other)
    {
        return Window == other.Window && Focus == other.Focus && Thread == other.Thread;
    }

    public TargetKind Kind()
    {
        string name = WindowsInput.WindowClass(Window);
        if (name == "CASCADIA_HOSTING_WINDOW_CLASS" || name == "CASCADIA_HOSTING_WINDOW_CLASS_WPF")
            return TargetKind.Terminal;
        if (name == "ConsoleWindowClass") return TargetKind.Console;
        return TargetKind.Editor;
    }
}

internal static class WindowsInput
{
    internal static readonly UIntPtr Marker = new UIntPtr(0x4C534E50);
    internal const int Shift = 1, Control = 2, Alt = 4, Win = 8;

    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyEvent
    {
        public uint Key, Scan, Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort Key, Scan;
        public uint Flags, Time;
        public UIntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr Extra;
    }

    internal delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
    internal delegate void WinEvent(IntPtr hook, uint type, IntPtr window, int obj, int child, uint thread, uint time);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern short GetKeyState(int key);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] internal static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] internal static extern bool IsWindowUnicode(IntPtr window);
    [DllImport("user32.dll")] internal static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool PostMessage(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern short VkKeyScanEx(char character, IntPtr layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder buffer, int count, uint flags, IntPtr layout);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] input, int size);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern IntPtr GetEditSelection(IntPtr window, uint message, out int start, out int end, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)] internal static extern IntPtr SendEditText(IntPtr window, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);

    internal static bool TryGetEditSelection(IntPtr window, out int start, out int end)
    {
        UIntPtr result;
        return GetEditSelection(window, 0x00B0, out start, out end, 2, 100, out result) != IntPtr.Zero;
    }

    internal static string WindowClass(IntPtr window)
    {
        var name = new StringBuilder(160);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    internal static bool Down(int key) { return GetAsyncKeyState(key) < 0; }

    internal static bool RequestKeyboardLayout(InputTarget target, IntPtr layout)
    {
        const uint inputLanguageChangeRequest = 0x0050;
        IntPtr window = target.Focus == IntPtr.Zero ? target.Window : target.Focus;
        return PostMessage(window, inputLanguageChangeRequest, UIntPtr.Zero, layout);
    }

    internal static int Modifiers()
    {
        return (Down(0x10) ? Shift : 0) | (Down(0x11) ? Control : 0) |
            (Down(0x12) ? Alt : 0) | (Down(0x5B) || Down(0x5C) ? Win : 0);
    }

    internal static bool ModifierKey(int key)
    {
        return (key >= 0xA0 && key <= 0xA5) || (key >= 0x10 && key <= 0x12) || key == 0x5B || key == 0x5C;
    }

    internal static byte[] KeyState(int modifiers, bool altGr, bool capsLock)
    {
        var state = new byte[256];
        if ((modifiers & Shift) != 0) state[0x10] = state[0xA0] = 128;
        if (altGr) state[0x11] = state[0xA2] = state[0x12] = state[0xA5] = 128;
        if (capsLock) state[0x14] = 1;
        return state;
    }

    internal static void Key(List<Input> input, Keys key, bool release)
    {
        uint flags = release ? 2u : 0;
        if (key == Keys.Insert || key == Keys.Delete || key == Keys.LWin || key == Keys.RWin ||
            key == Keys.RControlKey || key == Keys.RMenu || key >= Keys.Prior && key <= Keys.Down)
            flags |= 1;
        input.Add(Stroke((ushort)key, 0, flags));
    }

    private static Input Stroke(ushort key, ushort scan, uint flags)
    {
        return new Input { Type = 1, Data = new InputData {
            Keyboard = new KeyboardInput { Key = key, Scan = scan, Flags = flags, Extra = Marker }
        } };
    }

    internal static void Chord(List<Input> input, Keys key, int modifiers)
    {
        if ((modifiers & Control) != 0) Key(input, Keys.ControlKey, false);
        if ((modifiers & Shift) != 0) Key(input, Keys.ShiftKey, false);
        if ((modifiers & Alt) != 0) Key(input, Keys.Menu, false);
        if ((modifiers & Win) != 0) Key(input, Keys.LWin, false);
        Key(input, key, false);
        Key(input, key, true);
        if ((modifiers & Win) != 0) Key(input, Keys.LWin, true);
        if ((modifiers & Alt) != 0) Key(input, Keys.Menu, true);
        if ((modifiers & Shift) != 0) Key(input, Keys.ShiftKey, true);
        if ((modifiers & Control) != 0) Key(input, Keys.ControlKey, true);
    }

    internal static bool Send(List<Input> input)
    {
        return SendInput((uint)input.Count, input.ToArray(), Marshal.SizeOf(typeof(Input))) == input.Count;
    }

    internal static List<Input> Backspaces(int count)
    {
        var input = new List<Input>();
        for (int i = 0; i < count; i++) Chord(input, Keys.Back, 0);
        return input;
    }

    internal static void Text(List<Input> input, string text)
    {
        foreach (char character in text)
        {
            input.Add(Stroke(0, character, 4));
            input.Add(Stroke(0, character, 6));
        }
    }
}
