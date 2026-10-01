using System;
using System.Drawing;
using System.Windows.Forms;

namespace Slate;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart;

    public TrayIcon(Action summon, Action openConfig, Action reload, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Summon bar", null, (_, _) => summon());
        menu.Items.Add("Open config", null, (_, _) => openConfig());
        menu.Items.Add("Reload config", null, (_, _) => reload());

        _autostart = new ToolStripMenuItem("Run at startup") { Checked = Autostart.IsEnabled };
        _autostart.Click += (_, _) =>
        {
            Autostart.Set(!Autostart.IsEnabled);
            RefreshAutostart();
        };
        menu.Items.Add(_autostart);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        menu.Opening += (_, _) => RefreshAutostart();

        _icon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "Slate",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) summon();
        };
    }

    public void Notify(string title, string text) =>
        _icon.ShowBalloonTip(4000, title, text, ToolTipIcon.Info);

    public void RefreshAutostart() => _autostart.Checked = Autostart.IsEnabled;

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
