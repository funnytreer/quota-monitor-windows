using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;

namespace QuotaMonitor;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "QuotaMonitor";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) is string { Length: > 0 };
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }

        string process = Environment.ProcessPath ?? throw new IOException("Cannot identify the executable.");
        string command = BuildCommand(process, Assembly.GetEntryAssembly()?.Location);
        if (command.Length > 260) throw new IOException("The startup command is too long.");
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(EntryName, command, RegistryValueKind.String);
    }

    internal static string BuildCommand(string process, string? entryAssembly)
    {
        if (!string.Equals(Path.GetFileName(process), "dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return $"\"{process}\"";
        if (string.IsNullOrEmpty(entryAssembly)) throw new IOException("Cannot identify the entry assembly.");
        return $"\"{process}\" \"{entryAssembly}\"";
    }
}
