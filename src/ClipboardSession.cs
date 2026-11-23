using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;

internal sealed class ClipboardSession : IDisposable
{
    private const uint UnicodeText = 13;
    private const int MaxSnapshotBytes = 64 * 1024 * 1024;
    private static readonly uint ExcludeFromHistory = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly IntPtr owner;
    private readonly List<Format> saved = new List<Format>();
    private uint sequence;
    private bool hasSnapshot, changed;

    private ClipboardSession(IntPtr owner) { this.owner = owner; }

    private sealed class Format : IDisposable
    {
        internal readonly uint Id;
        internal IntPtr Handle;

        internal Format(uint id, IntPtr handle) { Id = id; Handle = handle; }

        internal static Format Copy(uint id, IntPtr source, ref int budget)
        {
            if (source == IntPtr.Zero) return null;
            if (id == 2 || id == 0x82) return new Format(id, OleDuplicateData(source, 2, 0));
            if (id == 3 || id == 0x83) return new Format(id, OleDuplicateData(source, 3, 0));
            if (id == 9) return new Format(id, OleDuplicateData(source, 9, 0));
            if (id == 14 || id == 0x8E) return new Format(id, CopyEnhMetaFile(source, null));
            // Those formats can contain process-specific handles or pointers.
            if (id == 0x80 || id >= 0x200 && id <= 0x3FF) return null;
            ulong size = GlobalSize(source).ToUInt64();
            if (size == 0 || size > (ulong)budget) return null;
            budget -= (int)size;
            return new Format(id, OleDuplicateData(source, (ushort)id, 0));
        }

        internal static Format FromBytes(uint id, byte[] bytes)
        {
            var format = new Format(id, GlobalAlloc(2, (UIntPtr)bytes.Length));
            if (format.Handle == IntPtr.Zero) return format;
            IntPtr destination = GlobalLock(format.Handle);
            if (destination == IntPtr.Zero) { format.Dispose(); return format; }
            try { Marshal.Copy(bytes, 0, destination, bytes.Length); }
            finally { GlobalUnlock(format.Handle); }
            return format;
        }

        internal bool Publish()
        {
            if (Handle == IntPtr.Zero || SetClipboardData(Id, Handle) == IntPtr.Zero) return false;
            Handle = IntPtr.Zero;
            return true;
        }

        public void Dispose()
        {
            if (Handle == IntPtr.Zero) return;
            if (Id == 2 || Id == 0x82 || Id == 9) DeleteObject(Handle);
            else if (Id == 3 || Id == 0x83)
            {
                var medium = new STGMEDIUM { tymed = TYMED.TYMED_MFPICT, unionmember = Handle };
                ReleaseStgMedium(ref medium);
            }
            else if (Id == 14 || Id == 0x8E) DeleteEnhMetaFile(Handle);
            else GlobalFree(Handle);
            Handle = IntPtr.Zero;
        }
    }

    internal static ClipboardSession Capture(IntPtr owner, Func<bool> current, out bool busy)
    {
        var clipboard = new ClipboardSession(owner);
        busy = !clipboard.Open(current);
        if (busy) return null;
        bool captured = false;
        try
        {
            int budget = MaxSnapshotBytes;
            uint id = 0;
            while (current())
            {
                // .NET Framework does not clear the thread's last error before a P/Invoke.
                SetLastError(0);
                id = EnumClipboardFormats(id);
                if (id == 0)
                {
                    if (Marshal.GetLastWin32Error() != 0) return null;
                    clipboard.sequence = GetClipboardSequenceNumber();
                    clipboard.hasSnapshot = true;
                    captured = true;
                    return clipboard;
                }
                var format = Format.Copy(id, GetClipboardData(id), ref budget);
                if (format == null || format.Handle == IntPtr.Zero) return null;
                clipboard.saved.Add(format);
            }
            return null;
        }
        finally
        {
            CloseClipboard();
            if (!captured) clipboard.Dispose();
        }
    }

    internal static bool WriteText(IntPtr owner, string text, Func<bool> current)
    {
        using (var clipboard = new ClipboardSession(owner) { sequence = GetClipboardSequenceNumber() })
            return clipboard.SetText(text, false, current);
    }

    private bool Open(Func<bool> current)
    {
        for (int tries = 0; tries < 16 && current(); tries++)
        {
            if (OpenClipboard(owner)) return true;
            if (tries < 15) Thread.Sleep(8);
        }
        return false;
    }

    internal bool IsCurrent() { return sequence == GetClipboardSequenceNumber(); }

    internal string ReadCopy(InputTarget target, Func<bool> current)
    {
        if (IsCurrent() || !Open(current)) return null;
        try
        {
            uint process, focusProcess;
            if (IsCurrent()) return null;
            WindowsInput.GetWindowThreadProcessId(GetClipboardOwner(), out process);
            WindowsInput.GetWindowThreadProcessId(target.Focus, out focusProcess);
            if (process == 0 || process != target.Process && process != focusProcess) return null;
            IntPtr handle = GetClipboardData(UnicodeText);
            sequence = GetClipboardSequenceNumber();
            changed = true;
            ulong bytes = GlobalSize(handle).ToUInt64();
            if (bytes < 2 || bytes > 2 * 1024 * 1024) return "";
            IntPtr data = GlobalLock(handle);
            if (data == IntPtr.Zero) return "";
            try
            {
                string text = Marshal.PtrToStringUni(data, (int)bytes / 2);
                int end = text.IndexOf('\0');
                return end < 0 ? text : text.Substring(0, end);
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    internal bool SetText(string text, bool temporary, Func<bool> current)
    {
        using (var data = Format.FromBytes(UnicodeText, Encoding.Unicode.GetBytes(text + "\0")))
        using (var exclusion = temporary ? Format.FromBytes(ExcludeFromHistory, new byte[] { 1 }) : null)
        {
            if (data.Handle == IntPtr.Zero || !Open(current)) return false;
            bool emptied = false;
            bool published = false;
            try
            {
                if (!IsCurrent() || !current() || !EmptyClipboard()) return false;
                emptied = true;
                changed = true;
                published = data.Publish();
                if (published && exclusion != null) exclusion.Publish();
                sequence = GetClipboardSequenceNumber();
            }
            finally { CloseClipboard(); }
            // Windows can synthesize formats and advance the sequence when the clipboard closes.
            if (emptied && GetClipboardOwner() == owner) sequence = GetClipboardSequenceNumber();
            else return false;
            if (published && !temporary) changed = false;
            return published;
        }
    }

    public void Dispose()
    {
        if (hasSnapshot && changed && IsCurrent() && Open(IsCurrent))
        {
            try
            {
                if (IsCurrent() && EmptyClipboard())
                    foreach (Format format in saved) format.Publish();
            }
            finally { CloseClipboard(); }
        }
        foreach (Format format in saved) format.Dispose();
        saved.Clear();
    }

    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("ole32.dll")] private static extern IntPtr OleDuplicateData(IntPtr source, ushort format, uint flags);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CopyEnhMetaFile(IntPtr source, string file);
    [DllImport("gdi32.dll")] private static extern bool DeleteEnhMetaFile(IntPtr handle);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
