using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

internal sealed class TrayNotification : NativeWindow, IDisposable
{
    internal const string GamingModeTooltip = "KeyRewind — Gaming Mode on";
    private const string WindowName = Program.Name + ".NotificationArea";
    private const string GamingProperty = Program.Name + ".GamingBadge";
    private const string RefreshPendingProperty = Program.Name + ".TrayRefreshPending";
    private const uint IconId = 1;
    private const int Callback = 0x8001;
    private const uint Add = 0, Modify = 1, Delete = 2;
    private const uint IconFlags = 7, InfoFlag = 0x10, InfoIcon = 1;
    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly TrayMenu menu;
    private readonly Action openSettings;
    private Icon artwork;
    private bool gaming;
    internal string Text { get; private set; }

    internal TrayNotification(Icon artwork, TrayMenu menu, Action openSettings)
    {
        this.artwork = artwork;
        this.menu = menu;
        this.openSettings = openSettings;
        Text = Program.Name;
    }

    internal void SetAppearance(bool gaming, Icon artwork, string text)
    {
        this.gaming = gaming;
        this.artwork = artwork;
        Text = text;
        if (Handle != IntPtr.Zero) Refresh();
    }

    internal void Show()
    {
        CreateHandle(new CreateParams());
        Refresh();
        SetWindowText(Handle, WindowName);
    }

    private void Refresh()
    {
        if (gaming) SetProp(Handle, GamingProperty, new IntPtr(1));
        else RemoveProp(Handle, GamingProperty);
        if (Register(Handle, artwork, Text)) RemoveProp(Handle, RefreshPendingProperty);
        else SetProp(Handle, RefreshPendingProperty, new IntPtr(1));
    }

    internal void ShowNotice(string message)
    {
        var data = new NotifyData(Handle) { Flags = InfoFlag, Info = message, Title = Program.Name, InfoFlags = InfoIcon };
        Shell_NotifyIcon(Modify, ref data);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == TaskbarCreated) Refresh();
        if (message.Msg == Callback)
        {
            switch (message.LParam.ToInt32())
            {
                case 0x0202: // WM_LBUTTONUP
                    openSettings();
                    break;
                case 0x0205: // WM_RBUTTONUP
                case 0x007B: // WM_CONTEXTMENU
                    SetForegroundWindow(Handle);
                    menu.Show(Cursor.Position);
                    WindowsInput.PostMessage(Handle, 0, UIntPtr.Zero, IntPtr.Zero);
                    break;
            }
            return;
        }
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        var data = new NotifyData(Handle);
        Shell_NotifyIcon(Delete, ref data);
        RemoveProp(Handle, GamingProperty);
        RemoveProp(Handle, RefreshPendingProperty);
        DestroyHandle();
    }

    internal static void RegisterAfterLaunch(Process child)
    {
        // The launcher can reach an elevated Explorer when the standard-user tray cannot.
        var startup = Stopwatch.StartNew();
        do
        {
            var window = FindWindow(null, WindowName);
            uint owner;
            if (WindowsInput.GetWindowThreadProcessId(window, out owner) != 0 &&
                (owner == child.Id && !child.HasExited || IsRunningCopy(owner)))
            {
                var id = new NotifyId { Size = Marshal.SizeOf(typeof(NotifyId)), Window = window, Id = IconId };
                Rectangle bounds;
                if (GetProp(window, RefreshPendingProperty) == IntPtr.Zero &&
                    Shell_NotifyIconGetRect(ref id, out bounds) == 0) return;
                bool gaming = GetProp(window, GamingProperty) != IntPtr.Zero;
                using (var stream = typeof(Program).Assembly.GetManifestResourceStream("KeyRewind.ico"))
                using (var icon = new Icon(stream, SystemInformation.SmallIconSize))
                using (var badge = gaming ? TrayIcons.WithGamingBadge(icon) : null)
                {
                    if (gaming) UiText.UseLanguage(Settings.ReadLanguage());
                    if (Register(window, badge ?? icon, gaming ? UiText.Get(GamingModeTooltip) : Program.Name)) return;
                }
            }
            if (child.HasExited) return;
            Thread.Sleep(50);
        } while (startup.ElapsedMilliseconds < 5000);
    }

    private static bool IsRunningCopy(uint owner)
    {
        try
        {
            using (var process = Process.GetProcessById((int)owner))
                return string.Equals(process.MainModule.FileName, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return false; }
    }

    private static bool Register(IntPtr window, Icon icon, string text)
    {
        var data = new NotifyData(window) { Flags = IconFlags, CallbackMessage = Callback, Icon = icon.Handle, Tip = text };
        return Shell_NotifyIcon(Modify, ref data) || Shell_NotifyIcon(Add, ref data);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyId
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int Size;
        public IntPtr Window;
        public uint Id, Flags, CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;

        public NotifyData(IntPtr window) : this()
        {
            Size = Marshal.SizeOf(typeof(NotifyData));
            Window = window;
            Id = IconId;
            Timeout = 1800;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyId id, out Rectangle bounds);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string message);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string caption);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
