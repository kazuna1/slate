using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Slate.Config;

namespace Slate;

/// <summary>
/// Folder-name completion for command arguments (e.g. "cc ani" → "cc animu").
/// Candidates come from <see cref="Projects"/>: folders you use, zoxide, project roots and discovered repos.
/// </summary>
internal sealed class Completer
{
    private readonly Projects _projects;
    private IReadOnlyList<string> _names = Array.Empty<string>();
    private IReadOnlyDictionary<string, string> _folders = new Dictionary<string, string>();
    private int _refreshing;

    public Completer(Projects projects) => _projects = projects;

    /// <summary>Rebuilds the candidate list in the background; cheap enough to call on every summon.</summary>
    public void Refresh(SlateConfig config)
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _projects.RefreshIfStale();

        Task.Run(() =>
        {
            try
            {
                // Keep the first (highest-ranked) occurrence of each folder name.
                var names = new List<string>();
                var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in _projects.All(config))
                {
                    string name = Path.GetFileName(path.TrimEnd('\\', '/'));
                    if (name.Length > 0 && folders.TryAdd(name, path)) names.Add(name);
                }
                _names = names;
                _folders = folders;
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

    /// <summary>The best-ranked folder whose name is exactly <paramref name="name"/>, from the cached list (instant).</summary>
    public string? ExactFolder(string name) => _folders.TryGetValue(name, out var path) ? path : null;

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
}
