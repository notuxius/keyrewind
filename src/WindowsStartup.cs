using Microsoft.Win32;
using System;
using System.IO;
using System.Security;
using System.Windows.Forms;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static string ReadCommand(string keyPath = RunKey)
    {
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            return key == null ? null : key.GetValue(Program.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    internal static string Save(bool enabled, string previousCommand, string keyPath = RunKey)
    {
        string command = enabled ? "\"" + Application.ExecutablePath + "\"" : null;
        if (string.Equals(command, previousCommand, StringComparison.OrdinalIgnoreCase)) return command;

        if (enabled)
        {
            if (command.Length > 260) throw new IOException("Move KeyRewind to a shorter path and try again.");
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue(Program.Name, command, RegistryValueKind.String);
        }
        else
        {
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                if (key != null) key.DeleteValue(Program.Name, false);
        }
        return command;
    }

    internal static bool IsAccessError(Exception error)
    {
        return error is IOException || error is UnauthorizedAccessException || error is SecurityException;
    }
}
