using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Slate.Config;

namespace Slate;

/// <summary>
/// Folder-name completion for command arguments (e.g. "cc ani" → "cc animu").
/// Candidates come from zoxide's ranked database plus any configured project roots.
/// </summary>
internal sealed class Completer
{
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private int _refreshing;

    /// <summary>Rebuilds the candidate list in the background; cheap enough to call on every summon.</summary>
    public void Refresh(SlateConfig config)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        bool useZoxide = config.UseZoxide;
        var roots = config.ProjectRoots.ToList();

        Task.Run(() =>
        {
            try
            {
                var paths = new List<string>();
                if (useZoxide) paths.AddRange(QueryZoxide());
                foreach (var root in roots) paths.AddRange(Subfolders(root));

                // Keep the first (highest-ranked) occurrence of each folder name.
                var names = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in paths)
                {
                    string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (name.Length > 0 && seen.Add(name)) names.Add(name);
                }
                _names = names;
            }
            catch (Exception ex)
            {
                Log.Error("Refreshing completions", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Prefix matches first (in rank order), then substring matches.</summary>
    public IReadOnlyList<string> Match(string prefix)
    {
        var names = _names;
        if (prefix.Length == 0) return names;
        var starts = names.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var contains = names.Where(n => !n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                        && n.Contains(prefix, StringComparison.OrdinalIgnoreCase));
        return starts.Concat(contains).ToList();
    }

    /// <summary>Quotes a name for PowerShell if it contains spaces.</summary>
    public static string Quote(string name) =>
        name.IndexOfAny(new[] { ' ', '\t', '\'', '&', '(', ')', ';', ',' }) >= 0
            ? "'" + name.Replace("'", "''") + "'"
            : name;

    private static IEnumerable<string> QueryZoxide()
    {
        try
        {
            var psi = new ProcessStartInfo("zoxide", "query -l")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return Array.Empty<string>();
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                p.Kill();
                return Array.Empty<string>();
            }
            return output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return Array.Empty<string>(); // zoxide not installed
        }
    }

    private static IEnumerable<string> Subfolders(string root)
    {
        try
        {
            string dir = Environment.ExpandEnvironmentVariables(root);
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
