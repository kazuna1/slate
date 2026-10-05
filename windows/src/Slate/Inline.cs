using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Slate.Config;

namespace Slate;

/// <summary>The outcome of an in-place command: one line for the bar, optionally copied, with the full output.</summary>
internal sealed record InlineResult(bool Ok, string Text, string? Copy = null, string? Log = null);

/// <summary>A process Slate runs in place; <see cref="Summary"/> turns its output into the result line.</summary>
internal sealed record InlineTask(
    string Title,
    string FileName,
    IReadOnlyList<string> Arguments,
    string Directory,
    Func<int, string, InlineResult> Summary,
    Func<string, double?>? Progress = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Either a process (<see cref="Task"/>) or work done in Slate itself (<see cref="Action"/>).</summary>
internal sealed record InlinePlan(string Title, InlineTask? Task = null, Func<InlineResult>? Action = null);

/// <summary>
/// Commands Slate runs in place, with no terminal: a progress strip in the bar, then ✓ or ✗.
/// Only quick, non-interactive commands qualify; everything else still opens a terminal, and
/// Shift+Enter forces a terminal for one command. Mirrors mac/Sources/Slate/Inline.swift.
/// </summary>
internal static class Inline
{
    private static readonly string[] Installers = ["winget", "npm", "pnpm", "yarn", "pip", "pipx", "scoop", "cargo", "gem"];
    private static readonly string[] InstallVerbs = ["install", "i", "add", "uninstall", "remove", "upgrade", "update"];

    // ---------------------------------------------------------------------
    // Deciding
    // ---------------------------------------------------------------------

    /// <summary>The in-place plan for <paramref name="text"/>, or null to use a terminal. No side effects (also used for the hint).</summary>
    public static InlinePlan? Plan(string text, SlateConfig config, Func<string, string?> resolveProject)
    {
        var words = ShellWords.Split(text);
        if (words.Count == 0) return null;
        string first = words[0];
        var rest = words.Skip(1).ToList();
        string dir = CommandRunner.ResolveWorkingDirectory(config.WorkingDirectory);
        string shell = CommandRunner.ShellPath(config.Shell);

        // "= 24*365": calculator.
        if (text.StartsWith('='))
        {
            string expr = text[1..];
            if (Calculator.Evaluate(expr) is not double value) return null;
            string shown = Calculator.Format(value);
            return new InlinePlan($"= {shown}", Action: () => new InlineResult(true, $"{expr.Trim()} = {shown}", Copy: shown));
        }

        // "@anything": run it here, show the last line of output.
        if (first.StartsWith('@'))
        {
            string command = text.TrimStart('@', ' ');
            if (command.Length == 0) return null;
            return Pwsh(command, command, shell, dir, (status, output) => Generic(status, output, "Done"));
        }

        switch (first.ToLowerInvariant())
        {
            case "mkdir" or "md" or "cp" or "copy" or "mv" or "move":
            {
                if (rest.Count == 0 || HasGlob(rest)) return null;
                var targets = rest.Where(w => !w.StartsWith('-')).ToList();
                if (targets.Count == 0) return null;
                string done = first.ToLowerInvariant() switch
                {
                    "mkdir" or "md" => "Created " + string.Join(", ", targets.Select(t => CommandRunner.Abbreviate(Absolute(t, dir)))),
                    "cp" or "copy" => "Copied to " + CommandRunner.Abbreviate(Absolute(targets[^1], dir)),
                    _ => "Moved to " + CommandRunner.Abbreviate(Absolute(targets[^1], dir)),
                };
                return Pwsh(text, text, shell, dir, (status, output) => Generic(status, output, done, useOutputOnSuccess: false));
            }

            case "touch":
            {
                var targets = rest.Where(w => !w.StartsWith('-')).ToList();
                if (targets.Count == 0 || HasGlob(rest)) return null;
                var paths = targets.Select(t => Absolute(t, dir)).ToList();
                return new InlinePlan(text, Action: () => Touch(paths));
            }

            case "rm" or "del" or "rmdir" or "trash":
            {
                // Moves to the Recycle Bin instead of deleting: a typo stays recoverable.
                var targets = rest.Where(w => !w.StartsWith('-')).ToList();
                if (targets.Count == 0 || HasGlob(rest)) return null;
                var paths = targets.Select(t => Absolute(t, dir)).ToList();
                return new InlinePlan("Moving to Recycle Bin", Action: () => Recycle(paths));
            }

            case "pull" or "push" or "status":
            {
                // "pull slate": git for a project by name.
                if (rest.Count != 1 || Commands.IsCommand(first) || resolveProject(rest[0]) is not string folder) return null;
                return Git(first.ToLowerInvariant(), folder, Path.GetFileName(folder.TrimEnd('\\')));
            }

            case "kill":
            {
                // "kill :3000" stops whatever listens on the port ("kill 3000" stays a normal kill of a PID).
                if (rest.Count != 1 || !rest[0].StartsWith(':') || !int.TryParse(rest[0][1..], out int port)) return null;
                return KillPort(port, shell, dir);
            }

            case "which":
                if (rest.Count != 1) return null;
                return QuickAnswer(text, $"(Get-Command {Quote(rest[0])} -ErrorAction Stop).Source", shell, dir);

            case "where" or "pwd" or "date" or "whoami" or "hostname":
                return QuickAnswer(text, first.Equals("where", StringComparison.OrdinalIgnoreCase) ? "where.exe " + string.Join(' ', rest.Select(Quote)) : text, shell, dir);
        }

        if (Installers.Contains(first, StringComparer.OrdinalIgnoreCase) && rest.Count > 0
            && InstallVerbs.Contains(rest[0], StringComparer.OrdinalIgnoreCase))
        {
            // JS package managers only for global installs; a project install belongs in a terminal in that project.
            bool global = rest.Contains("-g") || rest.Contains("--global") || (first == "yarn" && rest[0] == "global");
            if (first is "npm" or "pnpm" or "yarn" && !global) return null;
            string verb = rest[0];
            return Pwsh(text, text, shell, dir, (status, output) => Generic(status, output, $"{first} {verb} finished"));
        }

        // "node -v", "git --version": a one-line answer.
        if (words.Count == 2 && words[1] is "-v" or "-V" or "--version" or "version")
            return QuickAnswer(text, text, shell, dir);

        return null;
    }

    /// <summary>Clone with git's progress, then ✓ with the folder.</summary>
    public static InlinePlan Clone(string repo, string dir)
    {
        string name = repo[(repo.LastIndexOf('/') + 1)..];
        string target = Path.Combine(dir, name);
        var task = new InlineTask($"Cloning {repo}", "gh", ["repo", "clone", repo, "--", "--progress"], dir,
            (status, output) => status == 0
                ? new InlineResult(true, $"Cloned {repo} → {CommandRunner.Abbreviate(target)}")
                : new InlineResult(false, LastLine(output) ?? "Clone failed", Log: output),
            GitProgress,
            new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        return new InlinePlan(task.Title, Task: task);
    }

    // ---------------------------------------------------------------------
    // Running
    // ---------------------------------------------------------------------

    /// <summary>Runs a plan off the UI thread. <paramref name="update"/> gets progress (null = spinner); <paramref name="done"/> the result.</summary>
    public static void Run(InlinePlan plan, Action<double?> update, Action<InlineResult> done)
    {
        if (plan.Action is { } action)
        {
            Task.Run(() =>
            {
                InlineResult result;
                try { result = action(); }
                catch (Exception ex) { result = new InlineResult(false, ex.Message); }
                done(result);
            });
            return;
        }
        RunProcess(plan.Task!, update, done);
    }

    private static void RunProcess(InlineTask task, Action<double?> update, Action<InlineResult> done)
    {
        var psi = new ProcessStartInfo(task.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = task.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in task.Arguments) psi.ArgumentList.Add(a);
        if (task.Environment != null)
            foreach (var (k, v) in task.Environment) psi.Environment[k] = v;

        var output = new StringBuilder();
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void OnLine(object? _, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            string tail;
            lock (output)
            {
                output.AppendLine(e.Data);
                tail = e.Data;
            }
            if (task.Progress?.Invoke(tail) is double progress) update(progress);
        }
        p.OutputDataReceived += OnLine;
        p.ErrorDataReceived += OnLine;
        // Exited fires when the process ends, even if an app it launched still holds the output pipes.
        p.Exited += async (_, _) =>
        {
            await Task.Delay(150); // let the last lines arrive
            string text;
            lock (output) text = output.ToString();
            int code = p.ExitCode;
            p.Dispose();
            done(task.Summary(code, text));
        };
        try
        {
            p.Start();
            p.StandardInput.Close(); // anything that asks for input fails instead of hanging
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Win32Exception ex)
        {
            done(new InlineResult(false, $"{task.FileName}: {ex.Message}"));
        }
    }

    // ---------------------------------------------------------------------
    // Pieces
    // ---------------------------------------------------------------------

    private static InlinePlan Pwsh(string title, string command, string shell, string dir, Func<int, string, InlineResult> summary)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " + command +
            "; if (-not $?) { exit 1 }"));
        var task = new InlineTask(title, shell, ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded], dir, summary);
        return new InlinePlan(title, Task: task);
    }

    private static InlinePlan QuickAnswer(string title, string command, string shell, string dir) =>
        Pwsh(title, command, shell, dir, (status, output) =>
        {
            string? answer = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            return status == 0 && answer != null
                ? new InlineResult(true, answer, Copy: answer, Log: output)
                : new InlineResult(false, LastLine(output) ?? $"{title} failed", Log: output);
        });

    private static InlinePlan Git(string verb, string folder, string name)
    {
        string[] args = verb switch
        {
            "status" => ["status", "--short", "--branch"],
            "pull" => ["pull", "--progress"],
            _ => ["push", "--progress"],
        };
        var task = new InlineTask($"git {verb} · {name}", "git", args, folder, (status, output) =>
        {
            if (status != 0) return new InlineResult(false, $"{name}: {LastLine(output) ?? $"git {verb} failed"}", Log: output);
            return verb switch
            {
                "status" => new InlineResult(true, $"{name}: {StatusSummary(output)}", Log: output),
                "pull" => new InlineResult(true, $"{name}: {(output.Contains("Already up to date") ? "already up to date" : LastLine(output) ?? "pulled")}", Log: output),
                _ => new InlineResult(true, $"{name}: {(output.Contains("Everything up-to-date") ? "nothing to push" : "pushed")}", Log: output),
            };
        }, verb == "status" ? null : GitProgress, new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" });
        return new InlinePlan(task.Title, Task: task);
    }

    /// <summary>"## main...origin/main [ahead 1]" + file lines → "main · 2 changed · ahead 1" / "main · clean".</summary>
    private static string StatusSummary(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string branch = "?", extra = "";
        if (lines.Length > 0 && lines[0].StartsWith("## "))
        {
            string head = lines[0][3..];
            branch = head.Split("...")[0].Trim();
            int bracket = head.IndexOf('[');
            if (bracket >= 0) extra = " · " + head[(bracket + 1)..].TrimEnd(']');
        }
        int files = lines.Skip(1).Count();
        return $"{branch} · {(files == 0 ? "clean" : $"{files} changed")}{extra}";
    }

    private static InlinePlan KillPort(int port, string shell, string dir)
    {
        string script =
            $"$c = Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue; " +
            $"if (-not $c) {{ Write-Output 'Nothing is listening on port {port}'; exit 1 }}; " +
            "$c | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object { " +
            "$p = Get-Process -Id $_ -ErrorAction SilentlyContinue; Write-Output \"$($p.ProcessName) (pid $_)\"; Stop-Process -Id $_ -Force }";
        return Pwsh($"Stopping port {port}", script, shell, dir, (status, output) =>
        {
            string who = string.Join(", ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return status == 0 ? new InlineResult(true, $"Stopped {who}") : new InlineResult(false, LastLine(output) ?? $"Couldn't stop port {port}");
        });
    }

    private static InlineResult Touch(List<string> paths)
    {
        foreach (var path in paths)
        {
            if (File.Exists(path)) File.SetLastWriteTime(path, DateTime.Now);
            else File.Create(path).Dispose();
        }
        return new InlineResult(true, "Created " + string.Join(", ", paths.Select(CommandRunner.Abbreviate)));
    }

    private static InlineResult Recycle(List<string> paths)
    {
        var moved = new List<string>();
        foreach (var path in paths)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                string missing = $"No such file: {CommandRunner.Abbreviate(path)}";
                return new InlineResult(false, moved.Count == 0 ? missing : $"Moved {string.Join(", ", moved)} to Recycle Bin; then: {missing}");
            }
            if (!RecycleBin.Send(path)) return new InlineResult(false, $"Couldn't move {Path.GetFileName(path)} to the Recycle Bin");
            moved.Add(Path.GetFileName(path.TrimEnd('\\')));
        }
        return new InlineResult(true, $"Moved {string.Join(", ", moved)} to Recycle Bin");
    }

    private static InlineResult Generic(int status, string output, string ok, bool useOutputOnSuccess = true) =>
        status == 0
            ? new InlineResult(true, (useOutputOnSuccess ? LastLine(output) : null) ?? ok, Log: output)
            : new InlineResult(false, LastLine(output) ?? $"Failed (exit code {status})", Log: output);

    /// <summary>Last meaningful line, without git's progress noise.</summary>
    public static string? LastLine(string output) =>
        output.Replace('\r', '\n').Split('\n').Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0 && !l.Contains('%') && !l.StartsWith("remote:") && !l.StartsWith("At line:") && !l.StartsWith("+ "));

    /// <summary>git's "Receiving objects:  45% (..)" / "Resolving deltas: 80%" → overall 0..1.</summary>
    public static double? GitProgress(string line)
    {
        var m = Regex.Match(line, @"(\d+)%");
        if (!m.Success) return null;
        double n = double.Parse(m.Groups[1].Value);
        if (line.Contains("Resolving deltas")) return 0.8 + 0.2 * n / 100;
        if (line.Contains("Receiving objects") || line.Contains("Writing objects")) return 0.1 + 0.7 * n / 100;
        return 0.1 * n / 100;
    }

    private static string Absolute(string path, string dir)
    {
        string p = Environment.ExpandEnvironmentVariables(path);
        if (p.StartsWith('~')) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        return Path.IsPathRooted(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(dir, p));
    }

    private static bool HasGlob(IEnumerable<string> words) => words.Any(w => w.Contains('*') || w.Contains('?') || w.Contains('['));

    private static string Quote(string word) => "'" + word.Replace("'", "''") + "'";
}

/// <summary>Splits a command line into words, honouring '…' and "…" quotes.</summary>
internal static class ShellWords
{
    public static List<string> Split(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        bool hasWord = false;
        foreach (char c in text)
        {
            if (quote is char q)
            {
                if (c == q) quote = null; else current.Append(c);
            }
            else if (c is '\'' or '"')
            {
                quote = c;
                hasWord = true;
            }
            else if (c is ' ' or '\t')
            {
                if (hasWord) { words.Add(current.ToString()); current.Clear(); hasWord = false; }
            }
            else
            {
                current.Append(c);
                hasWord = true;
            }
        }
        if (hasWord) words.Add(current.ToString());
        return words;
    }
}

/// <summary>"= 24*365": + - * / % ^ and parentheses.</summary>
internal static class Calculator
{
    public static double? Evaluate(string text)
    {
        var chars = text.Replace(" ", "").Replace(",", "");
        if (chars.Length == 0) return null;
        int i = 0;
        double? v = Expression(chars, ref i);
        return v is double d && i == chars.Length && double.IsFinite(d) ? d : null;
    }

    public static string Format(double v) =>
        v == Math.Round(v) && Math.Abs(v) < 1e15 ? ((long)v).ToString() : v.ToString("G10", System.Globalization.CultureInfo.InvariantCulture);

    private static double? Expression(string s, ref int i)
    {
        if (Term(s, ref i) is not double v) return null;
        while (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            char op = s[i++];
            if (Term(s, ref i) is not double r) return null;
            v = op == '+' ? v + r : v - r;
        }
        return v;
    }

    private static double? Term(string s, ref int i)
    {
        if (Power(s, ref i) is not double v) return null;
        while (i < s.Length && "*/%x×÷".Contains(s[i]))
        {
            char op = s[i++];
            if (Power(s, ref i) is not double r) return null;
            v = op switch { '/' or '÷' => v / r, '%' => v % r, _ => v * r };
        }
        return v;
    }

    private static double? Power(string s, ref int i)
    {
        if (Unary(s, ref i) is not double b) return null;
        if (i < s.Length && s[i] == '^')
        {
            i++;
            return Power(s, ref i) is double e ? Math.Pow(b, e) : null;
        }
        return b;
    }

    private static double? Unary(string s, ref int i)
    {
        if (i < s.Length && s[i] == '-') { i++; return Unary(s, ref i) is double v ? -v : null; }
        if (i < s.Length && s[i] == '+') { i++; return Unary(s, ref i); }
        return Primary(s, ref i);
    }

    private static double? Primary(string s, ref int i)
    {
        if (i >= s.Length) return null;
        if (s[i] == '(')
        {
            i++;
            if (Expression(s, ref i) is not double v || i >= s.Length || s[i] != ')') return null;
            i++;
            return v;
        }
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
        return i > start && double.TryParse(s[start..i], System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : null;
    }
}

/// <summary>Sends a file or folder to the Recycle Bin without any dialogs.</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    public static bool Send(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0", // double-null-terminated list
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
