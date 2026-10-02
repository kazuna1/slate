using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Slate;

/// <summary>
/// Small Slate-styled window shown by <c>Slate.exe --update</c>: downloads the installer with a progress bar,
/// verifies it, then starts it silently (the installer replaces and relaunches Slate). On failure or Cancel
/// it starts the current Slate again, so you're never left without it.
/// </summary>
internal sealed class UpdateWindow : Window
{
    private static readonly Color Violet = Color.FromRgb(0x8B, 0x5C, 0xF6);
    private static readonly Color Text = Color.FromRgb(0xF5, 0xF0, 0xFF);
    private static readonly Color Muted = Color.FromRgb(0x8B, 0x7B, 0xB3);

    private readonly UpdateInfo _update;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _cancelButton = new();
    private bool _handedOff;

    public UpdateWindow(UpdateInfo update)
    {
        _update = update;

        Title = "Updating Slate";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Icon = LoadIcon();

        _title.Text = $"Updating Slate to {update.Version.ToString(3)}";
        _title.FontSize = 15;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(Text);

        _status.Text = "Starting download…";
        _status.FontSize = 12;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 6, 0, 12);
        _status.Foreground = new SolidColorBrush(Muted);

        _bar.Height = 6;
        _bar.Minimum = 0;
        _bar.Maximum = 1;
        _bar.IsIndeterminate = true;
        _bar.BorderThickness = new Thickness(0);
        _bar.Background = new SolidColorBrush(Color.FromArgb(0x40, Violet.R, Violet.G, Violet.B));
        _bar.Foreground = new LinearGradientBrush(Color.FromRgb(0xC0, 0x84, 0xFC), Violet, 0);

        _cancelButton.Content = "Cancel";
        _cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        _cancelButton.Margin = new Thickness(0, 14, 0, 0);
        _cancelButton.Padding = new Thickness(14, 4, 14, 4);
        _cancelButton.Foreground = new SolidColorBrush(Text);
        _cancelButton.Background = new SolidColorBrush(Color.FromArgb(0x30, Violet.R, Violet.G, Violet.B));
        _cancelButton.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, Violet.R, Violet.G, Violet.B));
        _cancelButton.Click += (_, _) => _cancel.Cancel();

        var stack = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        stack.Children.Add(_title);
        stack.Children.Add(_status);
        stack.Children.Add(_bar);
        stack.Children.Add(_cancelButton);

        Content = new Border
        {
            Margin = new Thickness(24),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x7C, 0x4D, 0xFF)),
            Background = new LinearGradientBrush(Color.FromRgb(0x2E, 0x10, 0x65), Color.FromRgb(0x12, 0x06, 0x2B), 0),
            Effect = new DropShadowEffect { Color = Violet, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.7 },
            Child = stack,
        };

        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Loaded += async (_, _) => await RunAsync();
    }

    private async Task RunAsync()
    {
        var progress = new Progress<(long Read, long? Total)>(p =>
        {
            double mb = p.Read / 1048576.0;
            if (p.Total is long total && total > 0)
            {
                _bar.IsIndeterminate = false;
                _bar.Value = (double)p.Read / total;
                _status.Text = $"Downloading… {mb:F1} / {total / 1048576.0:F1} MB  ({100.0 * p.Read / total:F0}%)";
            }
            else
            {
                _status.Text = $"Downloading… {mb:F1} MB";
            }
        });

        try
        {
            string path = await Updater.DownloadAsync(_update, Path.GetTempPath(), progress, _cancel.Token);

            _bar.IsIndeterminate = true;
            _cancelButton.IsEnabled = false;
            _status.Text = "Installing… Slate will restart by itself.";
            await Task.Delay(600); // let the message be seen
            Updater.RunInstaller(path);
            _handedOff = true;
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
            // user cancelled
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   or InvalidDataException or Win32Exception)
        {
            Log.Error("Installing update", ex);
            _bar.IsIndeterminate = false;
            _bar.Value = 0;
            _title.Text = "Update failed";
            _status.Text = ex.Message;
            _cancelButton.Content = "Close";
            _cancelButton.Click += (_, _) => Finish();
            return;
        }
        Finish();
    }

    /// <summary>Exits; if the installer didn't take over, brings the current Slate back.</summary>
    private void Finish()
    {
        if (!_handedOff)
        {
            try
            {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })?.Dispose();
            }
            catch (Win32Exception ex)
            {
                Log.Error("Restarting Slate", ex);
            }
        }
        Application.Current.Shutdown();
    }

    private static ImageSource? LoadIcon()
    {
        using var stream = typeof(UpdateWindow).Assembly.GetManifestResourceStream("Slate.ico");
        return stream == null ? null : System.Windows.Media.Imaging.BitmapFrame.Create(stream,
            System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
    }
}
