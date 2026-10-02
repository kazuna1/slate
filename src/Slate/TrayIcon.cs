using System;
using System.Drawing;
using System.Windows.Forms;

namespace Slate;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart;
    private readonly ToolStripMenuItem _update;
    private Action? _balloonClick;

    /// <param name="update">Checks for an update, or installs it if one was already found.</param>
    public TrayIcon(Action summon, Action openConfig, Action reload, Action update, Action exit,
        Func<string> currentTheme, Action<SlateTheme> applyTheme)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Summon bar", null, (_, _) => summon());
        menu.Items.Add("Open config", null, (_, _) => openConfig());
        menu.Items.Add("Reload config", null, (_, _) => reload());

        var themes = new ToolStripMenuItem("Themes");
        themes.DropDownItems.Add(new ToolStripMenuItem("More themes coming soon") { Enabled = false });
        themes.DropDownOpening += (_, _) => RebuildThemes(themes, currentTheme(), applyTheme);
        menu.Items.Add(themes);

        _autostart = new ToolStripMenuItem("Run at startup") { Checked = Autostart.IsEnabled };
        _autostart.Click += (_, _) =>
        {
            Autostart.Set(!Autostart.IsEnabled);
            RefreshAutostart();
        };
        menu.Items.Add(_autostart);

        _update = new ToolStripMenuItem("Check for updates");
        _update.Click += (_, _) => update();
        menu.Items.Add(_update);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) => RefreshAutostart();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = $"Slate {Updater.CurrentVersion.ToString(3)}",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) summon();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonClick;
            _balloonClick = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
    }

    /// <summary>Shows a notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Notify(string title, string text, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    private static void RebuildThemes(ToolStripMenuItem parent, string current, Action<SlateTheme> applyTheme)
    {
        parent.DropDownItems.Clear();
        if (Themes.All.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("More themes coming soon") { Enabled = false });
            return;
        }
        foreach (var theme in Themes.All)
        {
            var item = new ToolStripMenuItem(theme.Name) { Checked = theme.Name == current };
            item.Click += (_, _) => applyTheme(theme);
            parent.DropDownItems.Add(item);
        }
    }

    public void RefreshAutostart() => _autostart.Checked = Autostart.IsEnabled;

    public void SetUpdateAvailable(Version? version) =>
        _update.Text = version == null ? "Check for updates" : $"Install update {version.ToString(3)}";

    /// <summary>The icon embedded from Assets/slate.ico, at the tray's size.</summary>
    private static Icon CreateIcon()
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Slate.ico");
        return stream != null ? new Icon(stream, SystemInformation.SmallIconSize) : SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
