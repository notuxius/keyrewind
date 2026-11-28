using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class SettingsActivation : NativeWindow, IDisposable
{
    private const string WindowName = Program.Name + ".ShowSettings";
    private const int ShowSettingsMessage = 0x8000; // WM_APP
    private static readonly IntPtr MessageOnlyParent = new IntPtr(-3); // HWND_MESSAGE
    private readonly Action openSettings;

    internal SettingsActivation(Action openSettings)
    {
        this.openSettings = openSettings;
        CreateHandle(new CreateParams { Parent = MessageOnlyParent, Caption = WindowName });
        // A standard-user launcher can only ask this elevated window to show Settings.
        if (Program.RunningAsAdministrator && !ChangeWindowMessageFilterEx(Handle, ShowSettingsMessage, 1, IntPtr.Zero))
        {
            var error = new Win32Exception();
            DestroyHandle();
            throw error;
        }
    }

    internal static void Request()
    {
        // A closely timed launch can see the mutex before the first window is ready.
        var startup = Stopwatch.StartNew();
        do
        {
            IntPtr window = FindWindowEx(MessageOnlyParent, IntPtr.Zero, null, WindowName);
            uint process;
            if (window != IntPtr.Zero && WindowsInput.GetWindowThreadProcessId(window, out process) != 0)
            {
                AllowSetForegroundWindow(process);
                if (WindowsInput.PostMessage(window, ShowSettingsMessage, UIntPtr.Zero, IntPtr.Zero)) return;
            }
            Thread.Sleep(25);
        } while (startup.ElapsedMilliseconds < 2000);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == ShowSettingsMessage)
            openSettings();
        else
            base.WndProc(ref message);
    }

    public void Dispose() { DestroyHandle(); }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string caption);
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr changeInfo);
}
