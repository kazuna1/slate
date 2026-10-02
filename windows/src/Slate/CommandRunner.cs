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
    /// <summary>Commands starting with this run hidden, with no terminal window ("@code .").</summary>
    public const char BackgroundPrefix = '@';

    /// <param name="folder">Run inside this folder instead of the configured working directory.</param>
    /// <param name="command">May be empty: then the terminal just opens in the folder.</param>
    /// <param name="onBackgroundFailure">For "@" commands: called (on a worker thread) with the error if it fails.</param>
    public static void Run(string command, SlateConfig config, string? folder = null, Action<string>? onBackgroundFailure = null)
    {
        string shell = ResolveShell(config.Shell);
        string workDir = folder != null && Directory.Exists(folder) ? folder : ResolveWorkingDirectory(config.WorkingDirectory);

        if (command.TrimStart().StartsWith(BackgroundPrefix))
        {
            RunHidden(command.TrimStart()[1..], shell, workDir, onBackgroundFailure);
            return;
        }

        // -EncodedCommand takes base64 UTF-16LE, so quotes and special characters arrive untouched.
        string? encoded = command.Trim().Length == 0 ? null : Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
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

    /// <summary>
    /// Runs a launcher-style command ("code .", "explorer .") in a hidden shell that exits when the command
    /// returns, so no window is left behind. The profile is skipped for speed.
    /// </summary>
    private static void RunHidden(string command, string shell, string workDir, Action<string>? onFailure)
    {
        var psi = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            // No output redirection: the app it launches (VS Code) would inherit the pipes and keep them
            // open, so waiting would last as long as the app. The exit code is enough.
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));

        var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the shell.");
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            using (p)
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    Log.Write($"Background command \"{command}\" exited with code {p.ExitCode}");
                    onFailure?.Invoke($"\"{command}\" failed (exit code {p.ExitCode}). Run it without @ to see the error.");
                }
            }
        });
    }

    private static void AddShellArgs(ProcessStartInfo psi, string? shell, string? encoded)
    {
        if (shell != null) psi.ArgumentList.Add(shell);
        psi.ArgumentList.Add("-NoExit");
        if (encoded == null) return;
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

    /// <summary>The default folder (config <c>workingDirectory</c>), or home if it's missing.</summary>
    public static string ResolveWorkingDirectory(string setting) =>
        DefaultFolderExists(setting) ? Expand(setting) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static bool DefaultFolderExists(string setting) => Directory.Exists(Expand(setting));

    private static string Expand(string? setting)
    {
        string dir = Environment.ExpandEnvironmentVariables(setting ?? string.Empty).Trim();
        if (dir == "~" || dir.StartsWith("~\\") || dir.StartsWith("~/"))
            dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + dir[1..];
        dir = dir.TrimEnd('\\', '/');
        if (dir.Length == 2 && dir[1] == ':') dir += "\\";
        return dir;
    }

    /// <summary>"C:\\Users\\me\\code" → "~\\code", for display.</summary>
    public static string Abbreviate(string path)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }
}
