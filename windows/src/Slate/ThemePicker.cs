using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Slate.Config;

namespace Slate;

/// <summary>
/// The theme gallery: every theme rendered as a real Slate bar, two per row. Clicking one picks it.
/// Shown on first launch, from the tray (Themes → Theme gallery…) and with ":themes".
/// Mirrors mac/Sources/Slate/ThemePicker.swift.
/// </summary>
internal sealed class ThemePicker : Window
{
    private static ThemePicker? _open;
    private Action<SlateTheme?>? _onPick;

    /// <summary>Opens the gallery (or brings it forward). <paramref name="onPick"/> gets the theme, or null if closed without one.</summary>
    public static void ShowGallery(SlateConfig config, string hotkeyDisplay, bool welcome, Action<SlateTheme?> onPick)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new ThemePicker(config, hotkeyDisplay, welcome) { _onPick = onPick };
        _open.Closed += (_, _) =>
        {
            var callback = _open?._onPick;
            _open = null;
            callback?.Invoke(null); // closed without choosing
        };
        _open.Show();
        _open.Activate();
    }

    private ThemePicker(SlateConfig config, string hotkeyDisplay, bool welcome)
    {
        Title = welcome ? "Welcome to Slate" : "Slate themes";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0A, 0x14));
        Icon = LoadIcon();

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 28) };
        stack.Children.Add(new TextBlock
        {
            Text = welcome ? "Choose your look" : "Pick a theme",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
        });
        stack.Children.Add(new TextBlock
        {
            Text = welcome
                ? $"Press {hotkeyDisplay} anywhere to open Slate. You can change the theme any time from the tray icon."
                : "Click a bar to use it.",
            FontSize = 13,
            Margin = new Thickness(0, 4, 0, 20),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontFamily = new FontFamily("Segoe UI"),
        });

        var grid = new UniformGrid { Columns = 2 };
        foreach (var theme in Themes.All)
        {
            var themed = Clone(config);
            theme.Apply(themed.Appearance);
            var preview = new MainWindow(themed, new History(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "slate-preview-history.txt"), 1), hotkeyDisplay);
            BitmapSource image;
            try
            {
                image = preview.RenderBitmap("", Color.FromRgb(0x0C, 0x0A, 0x14));
            }
            catch (Exception ex)
            {
                Log.Error($"Rendering {theme.Name} preview", ex);
                continue;
            }
            finally
            {
                preview.Close();
            }
            grid.Children.Add(Card(theme, image, theme.Name == config.Theme));
        }
        stack.Children.Add(grid);
        Content = stack;
    }

    private UIElement Card(SlateTheme theme, BitmapSource image, bool selected)
    {
        // The render includes the whole glow margin (40 DIP a side at 2x); keep only a little of it.
        const int cut = 52;
        var cropped = new CroppedBitmap(image, new Int32Rect(cut, cut, image.PixelWidth - cut * 2, image.PixelHeight - cut * 2));

        var content = new StackPanel();
        content.Children.Add(new Image { Source = cropped, Width = 384, Stretch = Stretch.Uniform });
        content.Children.Add(new TextBlock
        {
            Text = theme.Name + (selected ? "  ✓" : ""),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(6, 8, 0, 2),
            Foreground = new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            FontFamily = new FontFamily("Segoe UI"),
        });

        var idleBorder = selected ? Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF) : Colors.Transparent;
        var card = new Border
        {
            Child = content,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 18, 18),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(2),
            BorderBrush = new SolidColorBrush(idleBorder),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        card.MouseEnter += (_, _) =>
        {
            card.Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF));
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF));
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = Brushes.Transparent;
            card.BorderBrush = new SolidColorBrush(idleBorder);
        };
        card.MouseLeftButtonUp += (_, _) => Pick(theme);
        return card;
    }

    private void Pick(SlateTheme theme)
    {
        var callback = _onPick;
        _onPick = null;
        Close();
        callback?.Invoke(theme);
    }

    /// <summary>A copy of the config, so applying a theme to a preview doesn't touch the real one.</summary>
    private static SlateConfig Clone(SlateConfig config) =>
        System.Text.Json.JsonSerializer.Deserialize<SlateConfig>(System.Text.Json.JsonSerializer.Serialize(config))!;

    private static ImageSource? LoadIcon()
    {
        using var stream = typeof(ThemePicker).Assembly.GetManifestResourceStream("Slate.ico");
        return stream == null ? null : BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
    }
}
