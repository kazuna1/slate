using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Slate;

/// <summary>
/// Bar decorations drawn under the text. "vines": two winding branches with leaves along the top-right and
/// bottom-left edges (Forest theme). Geometry is in a y-down box of the bar's size.
/// Mirrors mac/Sources/Slate/Decoration.swift.
/// </summary>
internal static class Decoration
{
    internal sealed record Vine(List<Point> Branch, List<List<Point>> Leaves);

    public static List<Vine> Vines(double w, double h, double r) =>
    [
        MakeVine(new Point(r * 0.5, h - 10), new Point(w * 0.4, h - 7), amplitude: 5, waves: 3, leaves: 15, seed: 1),
        MakeVine(new Point(w - r * 0.5, 10), new Point(w * 0.56, 7), amplitude: 5, waves: 2.5, leaves: 13, seed: 2),
    ];

    /// <summary>A gently waving branch from <paramref name="a"/> to <paramref name="b"/> with leaves alternating sides.</summary>
    private static Vine MakeVine(Point a, Point b, double amplitude, double waves, int leaves, int seed)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
        var normal = new Point(-dy / length, dx / length);
        Point At(double t)
        {
            double s = amplitude * Math.Sin(2 * Math.PI * waves * t) * (0.4 + 0.6 * t); // thinner wave near the root
            return new Point(a.X + dx * t + normal.X * s, a.Y + dy * t + normal.Y * s);
        }

        var branch = Enumerable.Range(0, 61).Select(i => At(i / 60.0)).ToList();
        var leafList = new List<List<Point>>();
        for (int i = 0; i < leaves; i++)
        {
            double t = 0.1 + 0.85 * i / Math.Max(1, leaves - 1);
            Point p = At(t), q = At(Math.Min(1, t + 0.01));
            double along = Math.Atan2(q.Y - p.Y, q.X - p.X);
            double side = (i + seed) % 2 == 0 ? 1 : -1;
            double angle = along + side * (Math.PI / 3.2);
            double size = 10 + 5 * ((i * 7 + seed * 3) % 5) / 4.0; // 10…15, varied but stable
            leafList.Add(Leaf(p, angle, size));
        }
        return new Vine(branch, leafList);
    }

    /// <summary>A pointed leaf: two arcs meeting at the tip, approximated with a polygon.</summary>
    private static List<Point> Leaf(Point basePoint, double angle, double length)
    {
        double width = length * 0.42;
        var outline = new List<Point>();
        for (int i = 0; i <= 10; i++) // upper edge, base → tip
        {
            double t = i / 10.0;
            outline.Add(new Point(length * t, width * Math.Sin(Math.PI * t) * (1 - 0.3 * t)));
        }
        for (int i = 9; i >= 0; i--) // lower edge, tip → base
        {
            double t = i / 10.0;
            outline.Add(new Point(length * t, -width * 0.8 * Math.Sin(Math.PI * t)));
        }
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return outline.Select(p => new Point(basePoint.X + p.X * c - p.Y * s, basePoint.Y + p.X * s + p.Y * c)).ToList();
    }
}
