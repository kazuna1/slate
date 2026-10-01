using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Windows;
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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        _tray = new TrayIcon(() => _bar.Summon(), OpenConfig, Reload, Quit);

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

        if (configError != null) Notify("Slate: config.json has an error", configError + "\nUsing defaults.");
        if (hotkeyError != null) Notify("Slate: bad hotkey", hotkeyError);
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
            case "help":
                Notify("Slate commands", ":config  :reload  :autostart on|off  :history clear  :exit");
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

    private void Quit()
    {
        _hook?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _bar?.Close();
        _singleInstance?.ReleaseMutex();
        Shutdown();
    }
}
