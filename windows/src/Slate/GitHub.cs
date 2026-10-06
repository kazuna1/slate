using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Slate;

/// <summary>
/// Every GitHub repo your <c>gh</c> login can access (your own, ones you collaborate on, orgs),
/// so "git clone ladder" works without a URL. Cached in repos.json, refreshed in the background.
/// Mirrors mac/Sources/Slate/GitHub.swift.
/// </summary>
internal sealed class GitHubRepos
{
    private static readonly string CachePath = Path.Combine(Paths.AppDir, "repos.json");
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(10);

    private sealed class Cache
    {
        public DateTime FetchedAt { get; set; }
        public List<string> Repos { get; set; } = []; // "owner/name"
    }

    private readonly object _lock = new();
    private Cache _cache;
    private int _fetching;

    /// <summary>One repo list for the whole app.</summary>
    public static GitHubRepos Shared => SharedInstance.Value;
    private static readonly Lazy<GitHubRepos> SharedInstance = new(() => new GitHubRepos());

    public GitHubRepos()
    {
        try
        {
            _cache = File.Exists(CachePath) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CachePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _cache = new();
        }
    }

    public IReadOnlyList<string> All
    {
        get { lock (_lock) return _cache.Repos.ToList(); }
    }

    /// <summary>Refreshes the list in the background if it's older than 10 minutes.</summary>
    public void RefreshIfStale()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cache.FetchedAt < RefreshAfter) return;
        }
        if (Interlocked.Exchange(ref _fetching, 1) == 1) return;
        Task.Run(() =>
        {
            try { Fetch(); }
            catch (Exception ex) { Log.Error("Listing GitHub repos", ex); }
        });
    }

    /// <summary>
    /// Repos matching a name ("ladder") or "owner/name". Exact names win over prefixes.
    /// If the cached list has no exact match, it asks GitHub again first: the repo may have been
    /// created after the last refresh. Call off the UI thread.
    /// </summary>
    public IReadOnlyList<string> Find(string query)
    {
        string q = query.Trim();
        if (q.Contains('/')) return [q]; // owner/name: gh clones it directly

        static string Name(string repo) => repo[(repo.LastIndexOf('/') + 1)..];
        List<string> Exact() => All.Where(r => Name(r).Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (Exact().Count == 0) Fetch();
        var exact = Exact();
        return exact.Count > 0 ? exact : All.Where(r => Name(r).StartsWith(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Repo names (or owner/name once a "/" is typed) for Tab completion.</summary>
    public IReadOnlyList<string> Completions(string prefix)
    {
        bool full = prefix.Contains('/');
        return All.Select(r => full ? r : r[(r.LastIndexOf('/') + 1)..])
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void Fetch()
    {
        try
        {
            var psi = new ProcessStartInfo("gh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "api", "user/repos?per_page=100&affiliation=owner,collaborator,organization_member",
                                      "--paginate", "--jq", ".[].full_name" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start gh.");
            var output = p.StandardOutput.ReadToEndAsync();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                Log.Write($"gh api user/repos failed: {error.Trim()}");
                throw new InvalidOperationException(error.Contains("auth") || error.Contains("login")
                    ? "GitHub CLI isn't logged in. Run: gh auth login"
                    : $"Couldn't list your GitHub repos: {error.Trim()}");
            }

            var repos = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            lock (_lock)
            {
                _cache = new Cache { FetchedAt = DateTime.UtcNow, Repos = repos };
                try { File.WriteAllText(CachePath, JsonSerializer.Serialize(_cache)); }
                catch (IOException ex) { Log.Error("Saving repo cache", ex); }
            }
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("GitHub CLI (gh) isn't installed. Install it with: winget install GitHub.cli");
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }
}
