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

    public ConfigStore()
    {
        if (!File.Exists(FilePath))
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new SlateConfig(), Options));

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
                return JsonSerializer.Deserialize<SlateConfig>(json, Options) ?? new SlateConfig();
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

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
