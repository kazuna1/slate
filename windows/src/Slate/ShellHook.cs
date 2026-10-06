using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Slate;

/// <summary>
/// "Learn from terminal": a small, clearly marked block in your PowerShell profile(s) that appends each
/// folder you <c>cd</c> into to visits.log. Slate folds that log into its folder ranking, so it learns from
/// every terminal, like zoxide. Off by default; Slate adds or removes only its own marked block.
/// </summary>
internal static class ShellHook
{
    public static readonly string VisitsLog = Path.Combine(Paths.AppDir, "visits.log");

    private const string Begin = "# >>> slate: learn folders >>>";
    private const string End = "# <<< slate: learn folders <<<";

    private static string Block => string.Join(Environment.NewLine,
        Begin,
        "# Added by Slate (tray > Learn from terminal). Remove with :learn off, or delete this block.",
        "$global:__slateLastDir = $null",
        "$global:__slatePrompt = $function:prompt",
        "function global:prompt {",
        "    $d = $PWD.ProviderPath",
        "    if ($d -and $d -ne $global:__slateLastDir) {",
        "        $global:__slateLastDir = $d",
        "        try { [IO.File]::AppendAllText(\"$env:APPDATA\\Slate\\visits.log\", \"$([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())`t$d`n\") } catch {}",
        "    }",
        "    & $global:__slatePrompt",
        "}",
        End);

    /// <summary>Windows PowerShell's profile, plus PowerShell 7's if you use it.</summary>
    private static IEnumerable<string> Profiles()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
        string pwsh = Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        if (File.Exists(pwsh) || Directory.Exists(Path.GetDirectoryName(pwsh))) yield return pwsh;
    }

    public static bool IsInstalled
    {
        get
        {
            foreach (var p in Profiles())
                if (File.Exists(p) && File.ReadAllText(p).Contains(Begin)) return true;
            return false;
        }
    }

    public static void Install()
    {
        foreach (var p in Profiles())
        {
            string text = File.Exists(p) ? File.ReadAllText(p) : string.Empty;
            if (text.Contains(Begin)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            string sep = text.Length == 0 || text.EndsWith('\n') ? string.Empty : Environment.NewLine;
            // UTF-8 with BOM: what Windows PowerShell 5 expects for non-ASCII profiles.
            File.WriteAllText(p, text + sep + Environment.NewLine + Block + Environment.NewLine, new UTF8Encoding(true));
        }
    }

    public static void Remove()
    {
        foreach (var p in Profiles())
        {
            if (!File.Exists(p)) continue;
            string text = File.ReadAllText(p);
            int start = text.IndexOf(Begin, StringComparison.Ordinal);
            int end = text.IndexOf(End, StringComparison.Ordinal);
            if (start < 0 || end < start) continue;
            end += End.Length;
            while (end < text.Length && (text[end] == '\r' || text[end] == '\n')) end++;
            while (start > 0 && (text[start - 1] == '\r' || text[start - 1] == '\n')) start--;
            File.WriteAllText(p, text[..start] + (start > 0 ? Environment.NewLine : string.Empty) + text[end..], new UTF8Encoding(true));
        }
    }
}
