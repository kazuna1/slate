using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace Slate;

internal static class Theme
{
    public static Color ParseColor(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(text.Trim());
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    public static Brush Solid(string? text, Color fallback)
    {
        var b = new SolidColorBrush(ParseColor(text, fallback));
        b.Freeze();
        return b;
    }

    public static Color WithAlpha(Color c, double alpha) =>
        Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    /// <summary>Evenly spaced gradient; one color gives a solid brush.</summary>
    public static Brush Gradient(IReadOnlyList<string>? stops, double angle, Color fallback)
    {
        if (stops == null || stops.Count == 0) return Solid(null, fallback);
        if (stops.Count == 1) return Solid(stops[0], fallback);

        var collection = new GradientStopCollection();
        for (int i = 0; i < stops.Count; i++)
            collection.Add(new GradientStop(ParseColor(stops[i], fallback), (double)i / (stops.Count - 1)));

        var brush = new LinearGradientBrush(collection, angle);
        brush.Freeze();
        return brush;
    }
}
