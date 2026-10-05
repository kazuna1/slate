using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Slate;

/// <summary>
/// Knows which words are real commands, so a project with the same name as a program
/// ("node", "git") never hijacks it: real commands always win.
/// </summary>
internal static class Commands
{
    // PowerShell aliases and cmd builtins that aren't files on PATH.
    private static readonly HashSet<string> ShellWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "ls", "dir", "cls", "clear", "echo", "cat", "type", "cp", "copy", "mv", "move", "rm", "del", "rmdir",
        "mkdir", "md", "pwd", "set", "exit", "start", "where", "history", "kill", "ps", "sleep", "sort", "tee",
        "write", "man", "help", "gc", "gci", "gi", "gl", "gm", "gps", "iex", "irm", "iwr", "ni", "ri", "sl", "sls",
        "measure", "select", "foreach", "if", "for", "while", "function", "return", "call", "pushd", "popd",
    };

    private static readonly Lazy<HashSet<string>> OnPath = new(Scan);

    /// <summary>True if <paramref name="word"/> runs something on its own (a program on PATH or a shell word).</summary>
    public static bool IsCommand(string word) =>
        ShellWords.Contains(word) || OnPath.Value.Contains(word);

    /// <summary>Names of every .exe/.cmd/.bat/.ps1 (etc.) on PATH, without extension. Scanned once.</summary>
    private static HashSet<string> Scan()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Append(".PS1")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir.Trim()))
                    if (exts.Contains(Path.GetExtension(file))) names.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // missing or unreadable PATH entry
            }
        }
        return names;
    }
}


/// <summary>
/// What typing a project name runs. <c>projectCommand</c> holds the command plus its default options
/// ("claude -c": continue the last conversation). Options you type replace the defaults
/// ("slate -r" → "claude -r"), and "-n" means "no defaults" ("slate -n" → "claude", a new conversation).
/// Mirrors mac/Sources/Slate/Commands.swift.
/// </summary>
internal static class ProjectCommand
{
    public static string Build(string projectCommand, string folder, string args)
    {
        var parts = projectCommand.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        string baseCmd = parts[0];
        string defaults = parts.Length > 1 ? parts[1] : string.Empty;

        if (args == "-n" || args.StartsWith("-n ")) return $"{baseCmd} {args[2..].Trim()}".Trim();
        if (args.Length > 0) return $"{baseCmd} {args}";

        // "claude -c" with nothing to continue would just fail: start a new conversation instead.
        var defaultWords = defaults.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (baseCmd.Equals("claude", StringComparison.OrdinalIgnoreCase) && defaultWords.Contains("-c") && !ClaudeSessions.Exist(folder))
            defaults = string.Join(' ', defaultWords.Where(w => w != "-c"));
        return $"{baseCmd} {defaults}".Trim();
    }
}

/// <summary>Claude Code stores a folder's conversations in ~/.claude/projects/&lt;path with non-alphanumerics as "-"&gt;.</summary>
internal static class ClaudeSessions
{
    public static bool Exist(string folder)
    {
        string slug = new(folder.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", slug);
        try
        {
            return System.IO.Directory.Exists(dir) && System.IO.Directory.EnumerateFiles(dir, "*.jsonl").Any();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
