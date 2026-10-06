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
            // Slate.exe --render-preview out.png ["text"] [theme]
            RenderPreview(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : "cc animu", e.Args.Length >= 4 ? e.Args[3] : null);
            return;
        }

        // Slate.exe --update <version> <installer-url> <sha256|->: the progress window started by the updater.
        if (e.Args.Length >= 4 && e.Args[0] == "--update")
        {
            var info = new UpdateInfo(Version.Parse(e.Args[1]), e.Args[2], e.Args[3] == "-" ? null : e.Args[3], string.Empty);
            new UpdateWindow(info).Show();
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

        Updater.DeleteOldInstallers();
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
            () => _config.Theme, ApplyTheme,
            () => CommandRunner.Abbreviate(CommandRunner.ResolveWorkingDirectory(_config.WorkingDirectory)), ChooseDefaultFolder,
            SetLearn);

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
            Notify("Slate is running", $"Press {hotkey.Display} anywhere. Commands run in your home folder: click here to choose a different one.",
                ChooseDefaultFolder);
        CheckDefaultFolder();
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
        try
        {
            // A separate Slate process shows the download; this one gets out of the way immediately.
            Updater.StartUpdater(_update);
            Quit();
        }
        catch (Win32Exception ex)
        {
            Log.Error("Starting updater", ex);
            Notify("Slate update failed", ex.Message);
            _updating = false;
        }
    }

    /// <summary>Writes a PNG of the bar using the current config, then exits. Doesn't touch a running instance.</summary>
    private void RenderPreview(string path, string text, string? theme)
    {
        int exitCode = 0;
        try
        {
            var config = new ConfigStore().TryLoad(out _) ?? new SlateConfig();
            foreach (var t in Themes.All)
                if (t.Name.Equals(theme, StringComparison.OrdinalIgnoreCase)) t.Apply(config.Appearance);
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
        CheckDefaultFolder();
    }

    private void HandleBuiltin(string text)
    {
        var parts = text[1..].Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        string rawArg = parts.Length > 1 ? parts[1] : string.Empty;
        string arg = rawArg.ToLowerInvariant();

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
            case "cd":
                ChangeDirectory(rawArg);
                return;
            case "learn" when arg is "on" or "off":
                SetLearn(arg == "on");
                return;
            case "version":
                Notify("Slate", $"Version {Updater.CurrentVersion.ToString(3)}");
                return;
            case "help":
                Notify("Slate commands", ":cd <folder>  :learn on|off  :config :reload  :update  :version  :autostart on|off  :history clear  :exit");
                return;
            default:
                Notify("Slate", $"Unknown command \"{text}\". Try :help");
                return;
        }
    }

    // ---------------------------------------------------------------------
    // Default folder
    // ---------------------------------------------------------------------

    private string? _warnedMissingFolder;

    /// <summary>Folder picker for where commands run, clones go, and which subfolders count as projects.</summary>
    private void ChooseDefaultFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Default folder for Slate: commands run here, \"git clone <name>\" clones here, and its folders become projects.",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = CommandRunner.ResolveWorkingDirectory(_config.WorkingDirectory),
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) SetDefaultFolder(dialog.SelectedPath);
    }

    private void SetDefaultFolder(string path)
    {
        _config.WorkingDirectory = path;
        _store?.Save(_config);
        Reload();
        Notify("Slate", $"Commands now run in {CommandRunner.Abbreviate(path)}.");
    }

    /// <summary>":cd" shows the default folder; ":cd &lt;path or project&gt;" sets it; ":cd ~" / "home" / "." resets it.</summary>
    private void ChangeDirectory(string arg)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (arg.Length == 0)
        {
            Notify("Default folder", $"{CommandRunner.Abbreviate(CommandRunner.ResolveWorkingDirectory(_config.WorkingDirectory))}. Change it with :cd <folder>, or from the tray menu.");
            return;
        }
        if (arg is "~" or "." || arg.Equals("home", StringComparison.OrdinalIgnoreCase))
        {
            SetDefaultFolder(home);
            return;
        }
        string expanded = Environment.ExpandEnvironmentVariables(arg.Trim('"', '\''));
        if (expanded.StartsWith('~')) expanded = home + expanded[1..];
        if (System.IO.Path.IsPathRooted(expanded) && System.IO.Directory.Exists(expanded)) SetDefaultFolder(expanded);
        else if (_bar?.ResolveFolder(arg) is string found) SetDefaultFolder(found);
        else Notify("Slate", $"No folder matches \"{arg}\".");
    }

    /// <summary>Says once (per folder) when the saved default folder is gone, instead of failing silently.</summary>
    private void CheckDefaultFolder()
    {
        string setting = _config.WorkingDirectory;
        if (CommandRunner.DefaultFolderExists(setting) || _warnedMissingFolder == setting) return;
        _warnedMissingFolder = setting;
        Notify("Default folder not found", $"{setting} is missing, so commands run in your home folder. Click to choose another.",
            ChooseDefaultFolder);
    }

    /// <summary>Adds or removes the "learn from terminal" block in your PowerShell profile.</summary>
    private void SetLearn(bool on)
    {
        try
        {
            if (on) ShellHook.Install(); else ShellHook.Remove();
            Notify("Slate", on
                ? "Slate now learns every folder you cd into (new terminals). Turn off from the tray or :learn off."
                : "Slate no longer learns from your terminals. Its block was removed from your PowerShell profile.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Changing terminal learning", ex);
            Notify("Slate", $"Couldn't change your PowerShell profile: {ex.Message}");
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
