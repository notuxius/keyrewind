using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;

internal static class StandardUserLaunch
{
    private const int SaferScopeUser = 2;
    private const int SaferLevelNormalUser = 0x20000;
    private const int SaferLevelOpen = 1;
    private const int TokenIntegrityLevel = 25;
    private const int GroupIntegrity = 0x20;
    private const int MediumIntegrity = 0x2000;
    private const int CreateNoWindow = 0x08000000;

    internal static bool RestartIfElevated()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (HasStandardRights(identity)) return false;

            IntPtr level;
            Check(SaferCreateLevel(SaferScopeUser, SaferLevelNormalUser, SaferLevelOpen, out level, IntPtr.Zero));
            try
            {
                SafeAccessTokenHandle token;
                Check(SaferComputeTokenFromLevel(level, identity.Token, out token, 0, IntPtr.Zero));
                using (token)
                {
                    // SAFER removes administrator rights but keeps the original integrity level.
                    SetMediumIntegrity(token);
                    using (var limited = new WindowsIdentity(token.DangerousGetHandle()))
                        if (!HasStandardRights(limited))
                            throw new Win32Exception("Windows did not return a standard-user token.");

                    var startup = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfo)) };
                    ProcessInfo child;
                    Check(CreateProcessAsUser(token, Application.ExecutablePath, new StringBuilder(Environment.CommandLine),
                        IntPtr.Zero, IntPtr.Zero, false, CreateNoWindow, IntPtr.Zero, null, ref startup, out child));
                    CloseHandle(child.Thread);
                    CloseHandle(child.Process);
                }
            }
            finally { SaferCloseLevel(level); }
            return true;
        }
    }

    private static bool HasStandardRights(WindowsIdentity identity)
    {
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return false;

        int size;
        GetTokenInformation(identity.Token, TokenIntegrityLevel, IntPtr.Zero, 0, out size);
        Check(size > 0);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Check(GetTokenInformation(identity.Token, TokenIntegrityLevel, buffer, size, out size));
            IntPtr integritySid = Marshal.ReadIntPtr(buffer);
            return Marshal.ReadInt32(GetSidSubAuthority(integritySid, 0)) <= MediumIntegrity;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void SetMediumIntegrity(SafeAccessTokenHandle token)
    {
        var sid = new SecurityIdentifier("S-1-16-8192");
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var label = new TokenLabel { Sid = pinned.AddrOfPinnedObject(), Attributes = GroupIntegrity };
            Check(SetTokenInformation(token, TokenIntegrityLevel, ref label, Marshal.SizeOf(typeof(TokenLabel)) + bytes.Length));
        }
        finally { pinned.Free(); }
    }

    private static void Check(bool ok)
    {
        if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenLabel
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string Reserved, Desktop, Title;
        public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr ReservedBytes, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferCreateLevel(int scope, int level, int flags, out IntPtr handle, IntPtr reserved);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SaferComputeTokenFromLevel(IntPtr level, IntPtr input, out SafeAccessTokenHandle output,
        int flags, IntPtr reserved);
    [DllImport("advapi32.dll")]
    private static extern bool SaferCloseLevel(IntPtr level);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int type, IntPtr buffer, int size, out int needed);
    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int type, ref TokenLabel label, int size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string executable, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int flags, IntPtr environment,
        string directory, ref StartupInfo startup, out ProcessInfo child);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
