using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Slate;

/// <summary>
/// Bar decorations drawn under the text, as a list of filled or stroked shapes coloured from the theme.
///  - "vines": two winding branches with leaves along the top-right and bottom-left edges (Forest).
///  - "dunes": layered sand dunes, a hazy sun and wind streaks (Dune).
///  - "stars": a faint nebula, scattered stars and sparkles (Galaxy).
/// Geometry is in a y-down box of the bar's size. Mirrors mac/Sources/Slate/Decoration.swift.
/// </summary>
internal static class Decoration
{
    /// <summary>Which theme colour a shape uses.</summary>
    internal enum Role { Border, Prompt, Highlight, Glow }

    /// <param name="LineWidth">null = filled (closed outlines); a value = stroked with that width.</param>
    /// <param name="Glow">Soft glow around the shape (the pearl).</param>
    internal sealed record Part(List<List<Point>> Paths, Role Role, double Alpha, double? LineWidth = null, bool Glow = false);

    public static List<Part> Parts(string name, double w, double h, double r) => name switch
    {
        "vines" => Vines(w, h, r),
        "dunes" => Dunes(w, h),
        "stars" => Stars(w, h),
        _ => [],
    };

    // ---------------------------------------------------------------------
    // Vines
    // ---------------------------------------------------------------------

    private static List<Part> Vines(double w, double h, double r)
    {
        var all = new[]
        {
            Vine(new Point(r * 0.5, h - 10), new Point(w * 0.4, h - 7), 5, 3, 15, 1),
            Vine(new Point(w - r * 0.5, 10), new Point(w * 0.56, 7), 5, 2.5, 13, 2),
        };
        var leaves = all.SelectMany(v => v.Leaves).ToList();
        return
        [
            new Part(all.Select(v => v.Branch).ToList(), Role.Border, 0.75, 1.8),
            new Part(leaves.Where((_, i) => i % 2 == 0).ToList(), Role.Prompt, 0.5),
            new Part(leaves.Where((_, i) => i % 2 == 1).ToList(), Role.Highlight, 0.32),
        ];
    }

    /// <summary>A gently waving branch from a to b with leaves alternating sides along it.</summary>
    private static (List<Point> Branch, List<List<Point>> Leaves) Vine(Point a, Point b, double amplitude, double waves, int leaves, int seed)
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
        var list = new List<List<Point>>();
        for (int i = 0; i < leaves; i++)
        {
            double t = 0.1 + 0.85 * i / Math.Max(1, leaves - 1);
            Point p = At(t), q = At(Math.Min(1, t + 0.01));
            double along = Math.Atan2(q.Y - p.Y, q.X - p.X);
            double side = (i + seed) % 2 == 0 ? 1 : -1;
            double size = 10 + 5 * ((i * 7 + seed * 3) % 5) / 4.0; // 10…15, varied but stable
            list.Add(Leaf(p, along + side * (Math.PI / 3.2), size));
        }
        return (branch, list);
    }

    /// <summary>A pointed leaf: two arcs meeting at the tip, approximated with a polygon.</summary>
    private static List<Point> Leaf(Point basePoint, double angle, double length)
    {
        double width = length * 0.42;
        var outline = new List<Point>();
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 10.0;
            outline.Add(new Point(length * t, width * Math.Sin(Math.PI * t) * (1 - 0.3 * t)));
        }
        for (int i = 9; i >= 0; i--)
        {
            double t = i / 10.0;
            outline.Add(new Point(length * t, -width * 0.8 * Math.Sin(Math.PI * t)));
        }
        return Transform(outline, basePoint, angle);
    }

    // ---------------------------------------------------------------------
    // Dunes
    // ---------------------------------------------------------------------

    /// <summary>Three layers of rolling sand dunes along the bottom, a hazy sun, wind streaks above the text.</summary>
    private static List<Part> Dunes(double w, double h)
    {
        List<Point> Ridge(double baseY, double height, double waves, double phase)
        {
            var pts = Enumerable.Range(0, 81).Select(i =>
            {
                double x = w * i / 80;
                // Two summed waves: long gentle swells with a sharper crest, like real dunes.
                double y = baseY - height * (0.6 * Math.Sin(2 * Math.PI * waves * x / w + phase) + 0.4 * Math.Sin(2 * Math.PI * waves * 2.3 * x / w + phase * 1.7));
                return new Point(x, y);
            }).ToList();
            pts.Add(new Point(w, h + 2));
            pts.Add(new Point(0, h + 2));
            return pts;
        }
        var sun = new Point(w * 0.6, h * 0.3);
        var streaks = new[] { (0.34, 0.46, 0.13), (0.4, 0.55, 0.2), (0.3, 0.4, 0.24) }.Select(z =>
            Enumerable.Range(0, 21).Select(i =>
            {
                double t = i / 20.0;
                return new Point(w * (z.Item1 + (z.Item2 - z.Item1) * t), h * z.Item3 + 1.2 * Math.Sin(t * Math.PI * 2));
            }).ToList()).ToList();
        var front = Ridge(h - 5, 3, 3.1, 4.0);
        return
        [
            new Part([CirclePoints(sun, 12)], Role.Glow, 0.12),
            new Part([CirclePoints(sun, 7.5)], Role.Prompt, 0.6, Glow: true),
            new Part([Ridge(h - 15, 5, 1.6, 0.6)], Role.Border, 0.32),
            new Part([Ridge(h - 10, 4.5, 2.3, 2.1)], Role.Prompt, 0.42),
            new Part([front], Role.Glow, 0.6),
            new Part([front.Take(front.Count - 2).ToList()], Role.Highlight, 0.4, 1),
            new Part(streaks, Role.Highlight, 0.22, 1),
        ];
    }

    // ---------------------------------------------------------------------
    // Stars
    // ---------------------------------------------------------------------

    /// <summary>A faint nebula, scattered stars of different sizes and a few four-point sparkles. Seeded, so stable.</summary>
    private static List<Part> Stars(double w, double h)
    {
        ulong seed = 0x51A7E;
        double Random()
        {
            seed = unchecked(seed * 6364136223846793005UL + 1442695040888963407UL);
            return (seed >> 33) % 10_000 / 10_000.0;
        }
        // Soft clouds: stacked ellipses shrinking toward the centre, so they fade out at the edges.
        List<List<Point>> Cloud(double fx, double fy, double rx, double ry) =>
            Enumerable.Range(0, 7).Select(i => { double k = 1 - i * 0.12; return Ellipse(new Point(w * fx, h * fy), w * rx * k, h * ry * k); }).ToList();
        var nebula = Cloud(0.62, 0.5, 0.2, 0.55).Concat(Cloud(0.25, 0.62, 0.09, 0.45)).ToList();
        var core = Cloud(0.66, 0.42, 0.08, 0.3);

        var small = new List<List<Point>>();
        var bright = new List<List<Point>>();
        for (int i = 0; i < 70; i++)
        {
            var p = new Point(Random() * w, Random() * h);
            double r = 0.35 + Random() * 0.8;
            (r > 0.95 ? bright : small).Add(CirclePoints(p, r, 8));
        }
        var sparkles = new[] { (0.07, 0.28), (0.47, 0.18), (0.9, 0.74), (0.33, 0.82) }.Select(z =>
        {
            var c = new Point(w * z.Item1, h * z.Item2);
            const double big = 4.5, thin = 0.9;
            return new List<Point>
            {
                new(c.X, c.Y - big), new(c.X + thin, c.Y - thin), new(c.X + big, c.Y), new(c.X + thin, c.Y + thin),
                new(c.X, c.Y + big), new(c.X - thin, c.Y + thin), new(c.X - big, c.Y), new(c.X - thin, c.Y - thin),
            };
        }).ToList();
        return
        [
            new Part(nebula, Role.Glow, 0.045),
            new Part(core, Role.Prompt, 0.04),
            new Part(small, Role.Highlight, 0.55),
            new Part(bright, Role.Highlight, 0.9, Glow: true),
            new Part(sparkles, Role.Highlight, 0.85, Glow: true),
        ];
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static double Dist(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static Point Offset(Point p, Point n, double d) => new(p.X + n.X * d, p.Y + n.Y * d);

    private static List<Point> CirclePoints(Point c, double r, int segments = 24) =>
        Enumerable.Range(0, segments).Select(i => { double a = i / (double)segments * 2 * Math.PI; return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }).ToList();

    private static List<Point> Ellipse(Point c, double rx, double ry) =>
        Enumerable.Range(0, 36).Select(i => { double a = i / 36.0 * 2 * Math.PI; return new Point(c.X + rx * Math.Cos(a), c.Y + ry * Math.Sin(a)); }).ToList();

    private static List<Point> Transform(List<Point> pts, Point basePoint, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return pts.Select(p => new Point(basePoint.X + p.X * c - p.Y * s, basePoint.Y + p.X * s + p.Y * c)).ToList();
    }
}
