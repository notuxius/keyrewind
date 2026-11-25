using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class UpdateHandoff
{
    private const string InstallSwitch = "--apply-update";
    private const string UpdatedSwitch = "--updated";
    private const string RestoredSwitch = "--update-restored";
    private const string GateName = @"Local\KeyRewind.Update";
    private const int ReadyTimeout = 5000;
    private const int ExitTimeout = 15000;
    private const int StartupTimeout = 15000;

    internal static Mutex EnterStartup()
    {
        var gate = new Mutex(false, GateName);
        try
        {
            Enter(gate);
            return gate;
        }
        catch
        {
            gate.Dispose();
            throw;
        }
    }

    internal static void Prepare(string target, byte[] payload, string digest)
    {
        if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
            throw new IOException("The KeyRewind executable is read-only.");
        string token = Guid.NewGuid().ToString("N");
        var update = new InstallFiles(target, token);
        using (var parent = Process.GetCurrentProcess())
        using (var ready = CreateSignal(token, "ready"))
        using (var commit = CreateSignal(token, "commit"))
        using (var started = CreateSignal(token, "started"))
        {
            try
            {
                // Staging beside the executable checks destination access and keeps replacement on the same volume.
                Directory.CreateDirectory(update.DirectoryPath);
                File.WriteAllBytes(update.Payload, payload);
                File.Copy(target, update.Helper);
                using (var helper = Start(update.Helper, InstallSwitch + " " + FormatId(parent.Id) + " " + token + " " + digest))
                {
                    if (!WaitForSignal(ready, helper, ReadyTimeout))
                    {
                        Stop(helper);
                        throw new IOException("KeyRewind could not prepare the updater.");
                    }
                    commit.Set();
                }
            }
            catch
            {
                update.CleanStaging();
                throw;
            }
        }
    }

    internal static bool TryRunInstaller(string[] args)
    {
        if (args.Length == 0 || args[0] != InstallSwitch) return false;
        int parentId;
        if (args.Length != 4 || !TryParseId(args[1], out parentId) || !IsToken(args[2]) || !ReleaseUpdate.IsDigest(args[3]))
            return true;
        try { RunInstaller(parentId, args[2], args[3]); }
        catch (Exception error)
        {
            if (!IsFailure(error)) throw;
            Debug.WriteLine(error.Message);
        }
        return true;
    }

    private static void RunInstaller(int parentId, string token, string digest)
    {
        using (var parent = Process.GetProcessById(parentId))
        using (var ready = EventWaitHandle.OpenExisting(SignalName(token, "ready")))
        using (var commit = EventWaitHandle.OpenExisting(SignalName(token, "commit")))
        using (var started = EventWaitHandle.OpenExisting(SignalName(token, "started")))
        using (var gate = new Mutex(false, GateName))
        {
            // Keep the real process handle before signaling the tray. The PID may be reused after shutdown.
            var update = new InstallFiles(parent.MainModule.FileName, token);
            if (!string.Equals(update.Helper, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) ||
                !ReleaseUpdate.VerifyFile(update.Payload, digest))
                return;

            try
            {
                Enter(gate);
                try
                {
                    ready.Set();
                    if (!commit.WaitOne(ReadyTimeout)) return;
                    if (!parent.WaitForExit(ExitTimeout))
                        throw new IOException("KeyRewind did not close. The update was cancelled.");
                    Install(update, token, started);
                }
                finally { gate.ReleaseMutex(); }
            }
            catch (Exception error)
            {
                if (!IsFailure(error)) throw;
                MessageBox.Show(error.Message + "\r\n\r\n" + update.DirectoryPath, Program.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private static void Install(InstallFiles update, string token, EventWaitHandle started)
    {
        string restartArgs = " " + token + " " + FormatId(ProcessId());
        try
        {
            File.Replace(update.Payload, update.Target, update.Backup);
            using (var child = Start(update.Target, UpdatedSwitch + restartArgs))
            {
                if (!WaitForSignal(started, child, StartupTimeout))
                {
                    Stop(child);
                    throw new IOException("The updated KeyRewind could not start.");
                }
            }
        }
        catch (Exception error)
        {
            if (!IsFailure(error)) throw;
            if (File.Exists(update.Backup))
            {
                if (File.Exists(update.Target)) File.Replace(update.Backup, update.Target, null);
                else File.Move(update.Backup, update.Target);
            }
            using (Start(update.Target, RestoredSwitch + restartArgs)) { }
            return;
        }

        Delete(update.Backup);
    }

    internal static Restart ReadRestart(string[] args)
    {
        int helperId;
        if (args.Length != 3 || (args[0] != UpdatedSwitch && args[0] != RestoredSwitch) ||
            !IsToken(args[1]) || !TryParseId(args[2], out helperId))
            return null;

        EventWaitHandle started = null;
        try
        {
            if (args[0] == UpdatedSwitch)
                started = EventWaitHandle.OpenExisting(SignalName(args[1], "started"));
            return new Restart(started, helperId, new InstallFiles(Application.ExecutablePath, args[1]));
        }
        catch (Exception error)
        {
            if (!IsFailure(error)) throw;
            if (started != null) started.Dispose();
            return null;
        }
    }

    internal sealed class Restart : IDisposable
    {
        private readonly EventWaitHandle started;
        private readonly int helperId;
        private readonly InstallFiles files;
        internal bool Installing { get { return started != null; } }

        internal Restart(EventWaitHandle started, int helperId, InstallFiles files)
        {
            this.started = started;
            this.helperId = helperId;
            this.files = files;
        }

        internal void Ready()
        {
            if (Installing) started.Set();
            Task.Run((Action)CleanAfterHelper);
        }

        private void CleanAfterHelper()
        {
            try
            {
                using (var helper = Process.GetProcessById(helperId))
                {
                    if (string.Equals(helper.MainModule.FileName, files.Helper, StringComparison.OrdinalIgnoreCase) &&
                        !helper.WaitForExit(ExitTimeout))
                        return;
                }
            }
            catch (Exception error)
            {
                if (!IsFailure(error)) throw;
                Debug.WriteLine(error.Message);
            }
            files.CleanStaging();
        }

        public void Dispose()
        {
            if (started != null) started.Dispose();
        }
    }

    internal sealed class InstallFiles
    {
        internal readonly string Target;
        internal readonly string DirectoryPath;
        internal readonly string Helper;
        internal readonly string Payload;
        internal readonly string Backup;

        internal InstallFiles(string target, string token)
        {
            Target = target;
            DirectoryPath = Path.Combine(Path.GetDirectoryName(target), ".KeyRewind-update-" + token);
            Helper = Path.Combine(DirectoryPath, "KeyRewind.Update.exe");
            Payload = Path.Combine(DirectoryPath, "download.exe");
            Backup = Path.Combine(DirectoryPath, "previous.exe");
        }

        internal void CleanStaging()
        {
            Delete(Helper);
            Delete(Payload);
            try { Directory.Delete(DirectoryPath); }
            catch (Exception error)
            {
                if (!IsFailure(error)) throw;
                Debug.WriteLine(error.Message);
            }
        }
    }

    private static void Enter(Mutex gate)
    {
        try
        {
            if (!gate.WaitOne(ExitTimeout + StartupTimeout + ReadyTimeout))
                throw new IOException("A KeyRewind update is still in progress.");
        }
        catch (AbandonedMutexException) { }
    }

    private static bool WaitForSignal(EventWaitHandle signal, Process process, int timeout)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            if (signal.WaitOne(50)) return !process.HasExited;
        } while (!process.HasExited && timer.ElapsedMilliseconds < timeout);
        return false;
    }

    private static void Stop(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(); }
        catch (InvalidOperationException) { }
        if (!process.WaitForExit(ReadyTimeout)) throw new IOException("The update process could not close.");
    }

    private static Process Start(string executable, string arguments)
    {
        return Process.Start(new ProcessStartInfo(executable, arguments) { UseShellExecute = false });
    }

    private static int ProcessId()
    {
        using (var process = Process.GetCurrentProcess()) return process.Id;
    }

    private static string FormatId(int id) { return id.ToString(CultureInfo.InvariantCulture); }

    private static bool TryParseId(string value, out int id)
    {
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private static bool IsToken(string value)
    {
        Guid token;
        return Guid.TryParseExact(value, "N", out token) && token.ToString("N") == value;
    }

    private static EventWaitHandle CreateSignal(string token, string name)
    {
        return new EventWaitHandle(false, EventResetMode.ManualReset, SignalName(token, name));
    }

    private static string SignalName(string token, string name) { return GateName + "." + token + "." + name; }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error)
        {
            if (!IsFailure(error)) throw;
            Debug.WriteLine(error.Message);
        }
    }

    internal static bool IsFailure(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is SecurityException ||
            error is Win32Exception || error is CryptographicException || error is InvalidOperationException ||
            error is ArgumentException || error is NotSupportedException || error is InvalidDataException ||
            error is WaitHandleCannotBeOpenedException;
    }
}
