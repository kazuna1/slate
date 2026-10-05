using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;

namespace Slate.Config;

/// <summary>Loads config.json and raises <see cref="Changed"/> when the file is saved.</summary>
internal sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;

    public string FilePath => Paths.Config;

    /// <summary>Raised on a thread-pool thread, debounced.</summary>
    public event Action? Changed;

    /// <summary>True on the very first run, when config.json had to be created.</summary>
    public bool CreatedNew { get; }

    public ConfigStore()
    {
        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SlateConfig(), Options));
            CreatedNew = true;
        }

        _debounce = new Timer(_ => Changed?.Invoke());
        _watcher = new FileSystemWatcher(Paths.AppDir, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += (_, _) => Poke();
        _watcher.Created += (_, _) => Poke();
        _watcher.Renamed += (_, _) => Poke();
        _watcher.EnableRaisingEvents = true;
    }

    private void Poke() => _debounce.Change(250, Timeout.Infinite);

    /// <summary>Returns null (and an error message) if the file can't be read or parsed.</summary>
    public SlateConfig? TryLoad(out string? error)
    {
        error = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var json = File.ReadAllText(FilePath);
                var config = JsonSerializer.Deserialize<SlateConfig>(json, Options) ?? new SlateConfig();
                if (Migrate(config)) Save(config);
                return config;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100); // editor still writing
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }
    }

    /// <summary>Upgrades settings saved by older versions. Returns true if anything changed.</summary>
    private static bool Migrate(SlateConfig config)
    {
        bool changed = false;
        // 1.3.4: the VS Code shortcut was renamed from "vc" to "vs".
        if (config.Shortcuts.Remove("vc", out var vsCode))
        {
            config.Shortcuts.TryAdd("vs", vsCode);
            changed = true;
        }
        // 1.3.5: VS Code opens without leaving a terminal behind.
        if (config.Shortcuts.TryGetValue("vs", out var vs) && vs == "code .")
        {
            config.Shortcuts["vs"] = "@code .";
            changed = true;
        }
        // 1.5.2: typing a project name continues the last conversation by default ("-n" starts a new one).
        if (config.ProjectCommand == "claude")
        {
            config.ProjectCommand = "claude -c";
            changed = true;
        }
        return changed;
    }

    public void Save(SlateConfig config)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(config, Options));
        }
        catch (IOException ex)
        {
            Log.Error("Saving config", ex);
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
