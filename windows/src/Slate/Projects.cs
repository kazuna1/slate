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
/// Finds folders by name, so "cc slate" works on any PC with no setup and no zoxide.
///
/// Known folders, ranked first: ones you've opened (through Slate, or in any terminal when "learn from
/// terminal" is on), ranked by frecency (how often and how recently); zoxide's list if installed;
/// subfolders of the default folder and <c>projectRoots</c>; git repos and their parent folders.
/// Then every other folder on your drives, from an index kept live by file-system watchers, so a
/// folder you created a second ago is found on the first try.
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
        ".next", ".cache", ".nuget", ".gradle",
    };

    private readonly object _lock = new();
    private Index _index;
    private int _scanning;
    private readonly List<FileSystemWatcher> _watchers = [];

    private sealed class Visit
    {
        public double Count { get; set; }
        public DateTime Last { get; set; }
    }

    private sealed class Index
    {
        public DateTime ScannedAt { get; set; }
        public List<string> Repos { get; set; } = [];
        /// <summary>Every folder on the scanned drives (not inside repos or junk folders).</summary>
        public List<string> Folders { get; set; } = [];
        /// <summary>Folder → how often and when it was last opened.</summary>
        public Dictionary<string, Visit> Visits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Pre-1.6 visit counts; migrated into <see cref="Visits"/> on load.</summary>
        public Dictionary<string, int>? Used { get; set; }
    }

    public Projects()
    {
        _index = Load();
        StartWatching();
    }

    // ---------------------------------------------------------------------
    // Scanning and watching
    // ---------------------------------------------------------------------

    /// <summary>Kicks off a background rescan if the cached one is stale.</summary>
    public void RefreshIfStale()
    {
        IngestTerminalVisits();
        if (DateTime.UtcNow - _index.ScannedAt < RescanAfter) return;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var repos = new List<string>();
                var folders = new List<string>();
                foreach (var (root, depth) in Roots()) Walk(root, depth, repos, folders);
                lock (_lock)
                {
                    _index.Repos = repos;
                    _index.Folders = folders;
                    _index.ScannedAt = DateTime.UtcNow;
                    Save();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Scanning folders", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _scanning, 0);
            }
        });
    }

    /// <summary>Your home folder, and every other fixed drive.</summary>
    private static IEnumerable<(string Root, int Depth)> Roots()
    {
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 6);
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            if (drive.RootDirectory.FullName.Equals(systemDrive, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (drive.RootDirectory.FullName, 7);
        }
    }

    private static void Walk(string dir, int depth, List<string> repos, List<string> folders)
    {
        if (depth < 0 || folders.Count > 200_000) return;
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (Skip(sub)) continue;
                folders.Add(sub);
                if (Directory.Exists(Path.Combine(sub, ".git")))
                {
                    repos.Add(sub); // a project: don't index its insides (src, docs, ...)
                    continue;
                }
                Walk(sub, depth - 1, repos, folders);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable folder: skip
        }
    }

    private static bool Skip(string path)
    {
        string name = Path.GetFileName(path);
        if (name.StartsWith('.') || name.StartsWith('$') || SkipDirs.Contains(name)) return true;
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Keeps the folder index current: new, renamed and deleted folders show up instantly.</summary>
    private void StartWatching()
    {
        foreach (var (root, _) in Roots())
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => OnFolderAdded(e.FullPath);
                w.Deleted += (_, e) => OnFolderRemoved(e.FullPath);
                w.Renamed += (_, e) =>
                {
                    OnFolderRemoved(e.OldFullPath);
                    OnFolderAdded(e.FullPath);
                };
                w.Error += (_, _) => { lock (_lock) _index.ScannedAt = DateTime.MinValue; }; // overflow: rescan next time
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Log.Error($"Watching {root}", ex);
            }
        }
    }

    private void OnFolderAdded(string path)
    {
        // Ignore anything under a junk or hidden folder (AppData churns constantly).
        foreach (var part in path.Split(Path.DirectorySeparatorChar))
            if (part.StartsWith('.') || part.StartsWith('$') || SkipDirs.Contains(part)) return;
        if (!Directory.Exists(path)) return; // a file, or already gone
        lock (_lock)
        {
            if (!_index.Folders.Contains(path, StringComparer.OrdinalIgnoreCase)) _index.Folders.Add(path);
        }
    }

    private void OnFolderRemoved(string path)
    {
        lock (_lock)
        {
            _index.Folders.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase)
                                          || f.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---------------------------------------------------------------------
    // Lookup
    // ---------------------------------------------------------------------

    /// <summary>Best folder for a query like "slate" or "new airlink", or null.</summary>
    public string? Resolve(string query, SlateConfig config)
    {
        query = query.Trim().Trim('\'', '"');
        if (query.Length == 0) return null;

        // A real path wins.
        string expanded = Environment.ExpandEnvironmentVariables(query);
        if (Path.IsPathRooted(expanded) && Directory.Exists(expanded)) return expanded;

        if (config.UseZoxide && Zoxide.Query(query) is string z && Directory.Exists(z)) return z;

        // Known folders first, then everything in the index (shallow paths first).
        List<string> indexed;
        lock (_lock)
        {
            indexed = _index.Folders.OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar)).ToList();
        }
        return Rank(Candidates(config).Concat(indexed).ToList(), query).FirstOrDefault(Directory.Exists);
    }

    /// <summary>Remembers a folder that was opened, so it ranks higher next time (and tells zoxide too).</summary>
    public void Visited(string folder, SlateConfig config)
    {
        lock (_lock)
        {
            Bump(folder, DateTime.UtcNow);
            Save();
        }
        if (config.UseZoxide) Zoxide.Add(folder);
    }

    /// <summary>Caller holds the lock.</summary>
    private void Bump(string folder, DateTime when)
    {
        if (!_index.Visits.TryGetValue(folder, out var v)) _index.Visits[folder] = v = new Visit();
        v.Count++;
        if (when > v.Last) v.Last = when;
    }

    /// <summary>Known folders, best first. Feeds Tab completion and bare project names ("slate").</summary>
    public IEnumerable<string> All(SlateConfig config) => Candidates(config);

    private List<string> Candidates(SlateConfig config)
    {
        var result = new List<string>();
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            result.AddRange(_index.Visits.OrderByDescending(kv => Frecency(kv.Value, now)).Select(kv => kv.Key));
        }
        if (config.UseZoxide) result.AddRange(Zoxide.List());
        foreach (var root in config.ProjectRoots) result.AddRange(Subfolders(root));
        // Projects in the default folder count too (not when it's home: that's Desktop, Documents, ...).
        string baseDir = CommandRunner.ResolveWorkingDirectory(config.WorkingDirectory);
        if (!baseDir.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase))
            result.AddRange(Subfolders(baseDir));
        lock (_lock)
        {
            result.AddRange(_index.Repos);
            // Folders that hold repos ("new airlink" holding "ndc") are often what people type.
            result.AddRange(_index.Repos.Select(Path.GetDirectoryName).OfType<string>()
                .Where(p => Path.GetPathRoot(p) != p));
        }
        return result.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>zoxide-style score: visit count weighted by how recent the last visit was.</summary>
    private static double Frecency(Visit v, DateTime now)
    {
        var age = now - v.Last;
        double weight = age < TimeSpan.FromHours(1) ? 4 : age < TimeSpan.FromDays(1) ? 2 : age < TimeSpan.FromDays(7) ? 0.5 : 0.25;
        return v.Count * weight;
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

    // ---------------------------------------------------------------------
    // Learning from terminals (ShellHook writes visits.log; we fold it in)
    // ---------------------------------------------------------------------

    private void IngestTerminalVisits()
    {
        string log = ShellHook.VisitsLog;
        string taken = log + ".reading";
        try
        {
            if (!File.Exists(log)) return;
            File.Move(log, taken, overwrite: true); // shells start a fresh file on their next cd
            var lines = File.ReadAllLines(taken);
            File.Delete(taken);
            lock (_lock)
            {
                foreach (var line in lines)
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || !long.TryParse(line[..tab], out long unix)) continue;
                    string path = line[(tab + 1)..].Trim();
                    if (path.Length > 3 && Directory.Exists(path)) Bump(path, DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime);
                }
                Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a shell is writing right now: next time
        }
    }

    // ---------------------------------------------------------------------
    // Persistence
    // ---------------------------------------------------------------------

    private static Index Load()
    {
        try
        {
            if (File.Exists(IndexPath))
            {
                var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(IndexPath)) ?? new Index();
                index.Visits = new Dictionary<string, Visit>(index.Visits, StringComparer.OrdinalIgnoreCase);
                if (index.Used != null)
                {
                    foreach (var (path, count) in index.Used)
                        index.Visits.TryAdd(path, new Visit { Count = count, Last = DateTime.UtcNow.AddDays(-1) });
                    index.Used = null;
                }
                if (index.Folders.Count == 0) index.ScannedAt = DateTime.MinValue; // pre-1.6 index: scan now
                return index;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Error("Loading project index", ex);
        }
        return new Index();
    }

    /// <summary>Caller holds the lock.</summary>
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
