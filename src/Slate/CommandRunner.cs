using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Slate.Config;

namespace Slate;

/// <summary>Opens a terminal running a PowerShell command, leaving it open afterwards.</summary>
internal static class CommandRunner
{
    public static void Run(string command, SlateConfig config)
    {
        // -EncodedCommand takes base64 UTF-16LE, so quotes and special characters arrive untouched.
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        string shell = ResolveShell(config.Shell);
        string workDir = ResolveWorkingDirectory(config.WorkingDirectory);
        string terminal = config.Terminal.Trim().ToLowerInvariant();

        if (terminal is "wt" or "wt-tab")
        {
            try
            {
                var psi = new ProcessStartInfo("wt.exe") { UseShellExecute = false, WorkingDirectory = workDir };
                psi.ArgumentList.Add("-w");
                psi.ArgumentList.Add(terminal == "wt-tab" ? "0" : "new");
                psi.ArgumentList.Add("new-tab");
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(workDir);
                AddShellArgs(psi, shell, encoded);
                Process.Start(psi)?.Dispose();
                return;
            }
            catch (Win32Exception ex)
            {
                // Windows Terminal not installed — fall back to a classic console window.
                Log.Error("Starting Windows Terminal", ex);
            }
        }

        var console = new ProcessStartInfo(shell) { UseShellExecute = false, WorkingDirectory = workDir };
        AddShellArgs(console, null, encoded);
        Process.Start(console)?.Dispose();
    }

    private static void AddShellArgs(ProcessStartInfo psi, string? shell, string encoded)
    {
        if (shell != null) psi.ArgumentList.Add(shell);
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
    }

    private static string ResolveShell(string setting)
    {
        switch (setting.Trim().ToLowerInvariant())
        {
            case "powershell" or "windowspowershell" or "powershell.exe":
                return "powershell.exe";
            case "pwsh" or "pwsh.exe":
                return "pwsh.exe";
            case "auto" or "":
                return AutoShell();
            default:
                return Environment.ExpandEnvironmentVariables(setting);
        }
    }

    /// <summary>Picks the shell whose profile exists, so personal functions like `c` and `v` load.</summary>
    private static string AutoShell()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        bool pwshProfile = File.Exists(Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1"))
                           || File.Exists(Path.Combine(docs, "PowerShell", "profile.ps1"));
        return pwshProfile && IsOnPath("pwsh.exe") ? "pwsh.exe" : "powershell.exe";
    }

    private static bool IsOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Any(dir =>
            {
                try { return File.Exists(Path.Combine(dir.Trim(), exe)); }
                catch (ArgumentException) { return false; }
            });

    private static string ResolveWorkingDirectory(string setting)
    {
        string dir = Environment.ExpandEnvironmentVariables(setting ?? string.Empty).TrimEnd('\\');
        if (dir.Length == 2 && dir[1] == ':') dir += "\\";
        return Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
