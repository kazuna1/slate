using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Slate.Config;
using Slate.Interop;

namespace Slate;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private ConfigStore? _store;
    private SlateConfig _config = new();
    private History? _history;
    private MainWindow? _bar;
    private HotkeyHook? _hook;
    private TrayIcon? _tray;

    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _update;
    private Version? _notifiedUpdate;
    private bool _updating;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 2 && e.Args[0] == "--render-preview")
        {
            RenderPreview(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : "cc animu");
            return;
        }

        _singleInstance = new Mutex(true, @"Local\Slate.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled", args.Exception);
            args.Handled = true;
        };

        _store = new ConfigStore();
        string? configError = null;
        _config = _store.TryLoad(out configError) ?? new SlateConfig();

        _history = new History(Paths.History, _config.HistorySize);
        var hotkey = ParseHotkey(_config.Hotkey, out string? hotkeyError);

        _bar = new MainWindow(_config, _history, hotkey.Display)
        {
            BuiltinHandler = HandleBuiltin,
            Notify = Notify,
            SaveConfig = c => _store.Save(c),
        };
        _bar.Show();

        _tray = new TrayIcon(() => _bar.Summon(), OpenConfig, Reload, () => _ = UpdateNowAsync(), Quit,
            () => _config.Theme, ApplyTheme);

        try
        {
            _hook = new HotkeyHook(hotkey);
            _hook.Pressed += () => Dispatcher.BeginInvoke(() => _bar.Toggle());
        }
        catch (Win32Exception ex)
        {
            Log.Error("Installing hotkey", ex);
            Notify("Slate", "Couldn't register the hotkey. Use the tray icon to summon the bar.");
        }

        _store.Changed += () => Dispatcher.BeginInvoke(Reload);

        if (_store.CreatedNew)
            Notify("Slate is running", $"Press {hotkey.Display} anywhere to summon the bar. Right-click the tray icon for options.");
        if (configError != null) Notify("Slate: config.json has an error", configError + "\nUsing defaults.");
        if (hotkeyError != null) Notify("Slate: bad hotkey", hotkeyError);

        // First check shortly after startup (not during login rush), then daily.
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(24);
            if (_config.CheckForUpdates) _ = CheckForUpdateAsync(manual: false);
        };
        _updateTimer.Start();
    }

    // ---------------------------------------------------------------------
    // Updates
    // ---------------------------------------------------------------------

    private async Task CheckForUpdateAsync(bool manual)
    {
        try
        {
            _update = await Updater.CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Log.Error("Checking for updates", ex);
            if (manual) Notify("Slate", "Couldn't check for updates. Are you online?");
            return;
        }

        _tray?.SetUpdateAvailable(_update?.Version);

        if (_update == null)
        {
            if (manual) Notify("Slate is up to date", $"You have the latest version ({Updater.CurrentVersion.ToString(3)}).");
            return;
        }

        // Nag once per version, not every day.
        if (manual || _notifiedUpdate != _update.Version)
        {
            _notifiedUpdate = _update.Version;
            Notify($"Slate {_update.Version.ToString(3)} is available",
                "Click to update now. Your settings are kept.",
                () => _ = UpdateNowAsync());
        }
    }

    /// <summary>Installs the known update, checking first if none is known yet.</summary>
    private async Task UpdateNowAsync()
    {
        if (_updating) return;
        if (_update == null)
        {
            await CheckForUpdateAsync(manual: true);
            return; // the notification it shows offers the install
        }

        if (!Updater.IsInstalled)
        {
            // Portable copy: no installer to update in place, so send them to the download.
            Process.Start(new ProcessStartInfo(_update.PageUrl) { UseShellExecute = true })?.Dispose();
            return;
        }

        _updating = true;
        Notify("Updating Slate", $"Downloading {_update.Version.ToString(3)}... Slate will restart by itself.");
        try
        {
            await Updater.DownloadAndRunAsync(_update);
            Quit(); // the installer replaces the exe and relaunches it
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or Win32Exception)
        {
            Log.Error("Installing update", ex);
            Notify("Slate update failed", ex.Message);
            _updating = false;
        }
    }

    /// <summary>Writes a PNG of the bar using the current config, then exits. Doesn't touch a running instance.</summary>
    private void RenderPreview(string path, string text)
    {
        int exitCode = 0;
        try
        {
            var config = new ConfigStore().TryLoad(out _) ?? new SlateConfig();
            var hotkey = ParseHotkey(config.Hotkey, out _);
            var window = new MainWindow(config, new History(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slate-preview-history.txt"), 1), hotkey.Display);
            window.RenderPreview(System.IO.Path.GetFullPath(path), text, System.Windows.Media.Color.FromRgb(0x0C, 0x0A, 0x14));
        }
        catch (Exception ex)
        {
            Log.Error("Rendering preview", ex);
            exitCode = 1;
        }
        Shutdown(exitCode);
    }

    private static Hotkey ParseHotkey(string text, out string? error)
    {
        error = null;
        try
        {
            return Hotkey.Parse(text);
        }
        catch (FormatException ex)
        {
            error = ex.Message + $" Using {Hotkey.DefaultText}.";
            return Hotkey.Parse(Hotkey.DefaultText);
        }
    }

    private void ApplyTheme(SlateTheme theme)
    {
        theme.Apply(_config.Appearance);
        _config.Theme = theme.Name;
        _store?.Save(_config);
        Reload();
    }

    private void Reload()
    {
        if (_store == null || _bar == null) return;

        var config = _store.TryLoad(out string? error);
        if (config == null)
        {
            Notify("Slate: config.json has an error", error + "\nKeeping the previous settings.");
            return;
        }

        _config = config;
        var hotkey = ParseHotkey(config.Hotkey, out string? hotkeyError);
        if (hotkeyError != null) Notify("Slate: bad hotkey", hotkeyError);
        if (_hook != null) _hook.Hotkey = hotkey;
        if (_history != null) _history.MaxSize = Math.Max(1, config.HistorySize);

        _bar.ApplyConfig(config, hotkey.Display);
    }

    private void HandleBuiltin(string text)
    {
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        string arg = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

        switch (cmd)
        {
            case "exit" or "quit":
                Quit();
                return;
            case "config":
                OpenConfig();
                return;
            case "reload":
                Reload();
                Notify("Slate", "Config reloaded.");
                return;
            case "history" when arg == "clear":
                _history?.Clear();
                Notify("Slate", "History cleared.");
                return;
            case "autostart" when arg is "on" or "off":
                Autostart.Set(arg == "on");
                _tray?.RefreshAutostart();
                Notify("Slate", arg == "on" ? "Slate will start with Windows." : "Slate won't start with Windows.");
                return;
            case "update":
                _ = UpdateNowAsync();
                return;
            case "version":
                Notify("Slate", $"Version {Updater.CurrentVersion.ToString(3)}");
                return;
            case "help":
                Notify("Slate commands", ":config  :reload  :update  :version  :autostart on|off  :history clear  :exit");
                return;
            default:
                Notify("Slate", $"Unknown command \"{text}\". Try :help");
                return;
        }
    }

    private void OpenConfig()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Paths.Config) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // No app associated with .json
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Paths.Config}\"") { UseShellExecute = true })?.Dispose();
        }
    }

    private void Notify(string title, string message) => _tray?.Notify(title, message);

    private void Notify(string title, string message, Action onClick) => _tray?.Notify(title, message, onClick);

    private void Quit()
    {
        _updateTimer?.Stop();
        _hook?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _bar?.Close();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}
