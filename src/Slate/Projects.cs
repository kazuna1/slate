using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Slate.Config;

namespace Slate;

/// <summary>
/// Finds project folders by name, so "cc slate" works on any PC with no setup.
/// Sources, best first: zoxide (if installed), folders you've opened through Slate,
/// subfolders of <c>projectRoots</c>, and git repositories Slate discovers on its own.
/// </summary>
internal sealed class Projects
{
    private static readonly string IndexPath = Path.Combine(Paths.AppDir, "projects.json");
    private static readonly TimeSpan RescanAfter = TimeSpan.FromHours(6);
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "build", "target", "packages", ".git", ".vs", ".idea",
        "AppData", "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "Recovery", "PerfLogs", "OneDriveTemp", "venv", ".venv", "__pycache__",
    };

    private readonly object _lock = new();
    private Index _index;
    private int _scanning;

    private sealed class Index
    {
        public DateTime ScannedAt { get; set; }
        public List<string> Repos { get; set; } = [];
        /// <summary>Folder → times opened through Slate.</summary>
        public Dictionary<string, int> Used { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public Projects()
    {
        _index = Load();
    }

    /// <summary>Kicks off a background rescan if the cached one is stale.</summary>
    public void RefreshIfStale()
    {
        if (DateTime.UtcNow - _index.ScannedAt < RescanAfter) return;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var repos = DiscoverRepos();
                lock (_lock)
                {
                    _index.Repos = repos;
                    _index.ScannedAt = DateTime.UtcNow;
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scanning for projects", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    /// <summary>Best folder for a query like "slate" or "new airlink", or null.</summary>
    public string? Resolve(string query, SlateConfig config)
    {
        query = query.Trim().Trim('\'', '"');
        if (query.Length == 0) return null;

        // A real path wins.
        string expanded = Environment.ExpandEnvironmentVariables(query);
        if (Path.IsPathRooted(expanded) && Directory.Exists(expanded)) return expanded;

        if (config.UseZoxide && Zoxide.Query(query) is string z && Directory.Exists(z)) return z;

        return Rank(Candidates(config), query).FirstOrDefault();
    }

    /// <summary>Remembers a folder that was opened, so it ranks higher next time (and tells zoxide too).</summary>
    public void Visited(string folder, SlateConfig config)
    {
        lock (_lock)
        {
            _index.Used[folder] = _index.Used.GetValueOrDefault(folder) + 1;
            Save();
        }
        if (config.UseZoxide) Zoxide.Add(folder);
    }

    /// <summary>All known folders, used folders first. Feeds Tab completion.</summary>
    public IEnumerable<string> All(SlateConfig config) => Candidates(config);

    private List<string> Candidates(SlateConfig config)
    {
        var result = new List<string>();
        lock (_lock)
        {
            result.AddRange(_index.Used.OrderByDescending(kv => kv.Value).Select(kv => kv.Key));
        }
        if (config.UseZoxide) result.AddRange(Zoxide.List());
        foreach (var root in config.ProjectRoots) result.AddRange(Subfolders(root));
        lock (_lock)
        {
            result.AddRange(_index.Repos);
            // Folders that hold repos ("new airlink" holding "ndc") are often what people type.
            result.AddRange(_index.Repos.Select(Path.GetDirectoryName).OfType<string>()
                .Where(p => Path.GetPathRoot(p) != p));
        }
        return result.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Exact name, then prefix, then contains; keeps source order (= rank) within each group.</summary>
    private static IEnumerable<string> Rank(List<string> folders, string query)
    {
        string Name(string p) => Path.GetFileName(p.TrimEnd('\\', '/'));
        var exact = folders.Where(f => Name(f).Equals(query, StringComparison.OrdinalIgnoreCase));
        var prefix = folders.Where(f => Name(f).StartsWith(query, StringComparison.OrdinalIgnoreCase));
        var contains = folders.Where(f => Name(f).Contains(query, StringComparison.OrdinalIgnoreCase));
        return exact.Concat(prefix).Concat(contains).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Git repositories under your home folder and every non-system drive.</summary>
    private static List<string> DiscoverRepos()
    {
        var found = new List<string>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Walk(home, 4, found);

        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (drive.RootDirectory.FullName.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)) continue;
            Walk(drive.RootDirectory.FullName, 5, found);
        }
        return found;
    }

    private static void Walk(string dir, int depth, List<string> found)
    {
        if (depth < 0 || found.Count > 5000) return;
        try
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
            {
                found.Add(dir);
                return; // don't descend into a repo
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                string name = Path.GetFileName(sub);
                if (name.StartsWith('.') || SkipDirs.Contains(name)) continue;
                var attrs = File.GetAttributes(sub);
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.System)) != 0) continue;
                Walk(sub, depth - 1, found);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable folder: skip
        }
    }

    private static IEnumerable<string> Subfolders(string root)
    {
        try
        {
            string dir = Environment.ExpandEnvironmentVariables(root);
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static Index Load()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath)) ?? new Index();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("Loading project index", ex);
        }
        return new Index();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(IndexPath, JsonSerializer.Serialize(_index));
        }
        catch (IOException ex)
        {
            Log.Error("Saving project index", ex);
        }
    }
}

/// <summary>Optional zoxide integration; everything returns empty when zoxide isn't installed.</summary>
internal static class Zoxide
{
    public static string? Query(string query)
    {
        var args = new List<string> { "query", "--" };
        args.AddRange(query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Run(args)?.Trim() is { Length: > 0 } s ? s : null;
    }

    public static IEnumerable<string> List() =>
        Run(["query", "-l"])?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

    public static void Add(string folder) => Task.Run(() => Run(["add", "--", folder]));

    private static string? Run(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo("zoxide")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(3000))
            {
                p.Kill();
                return null;
            }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (Win32Exception)
        {
            return null; // not installed
        }
    }
}
