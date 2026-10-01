using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
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

    /// <summary>Purple rounded square with a ❯, drawn at runtime so there's no asset to ship.</summary>
    private static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            using var path = RoundedRect(new RectangleF(1, 1, 30, 30), 8);
            using var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(32, 32),
                Color.FromArgb(0x4C, 0x1D, 0x95), Color.FromArgb(0x14, 0x08, 0x2A));
            g.FillPath(fill, path);
            using var pen = new Pen(Color.FromArgb(0xA7, 0x8B, 0xFA), 1.5f);
            g.DrawPath(pen, path);

            using var font = new Font("Segoe UI Symbol", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(0xE9, 0xD5, 0xFF));
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("❯", font, text, new RectangleF(0, 0, 32, 32), format);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
