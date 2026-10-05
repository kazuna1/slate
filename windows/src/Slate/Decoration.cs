using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Slate;

/// <summary>
/// Bar decorations drawn under the text, as a list of filled or stroked shapes coloured from the theme.
///  - "vines": two winding branches with leaves along the top-right and bottom-left edges (Forest).
///  - "dragon": a Chinese dragon spiralling around the bar, head at the top-left chasing a pearl (Dragon).
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
        "dragon" => Dragon(w, h),
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
    // Dragon
    // ---------------------------------------------------------------------

    /// <summary>
    /// The body runs clockwise around the bar (tail mid-top → right end → bottom → left end → head at the
    /// top-left facing right), waving, tapering, with spines on its back, scale marks and a belly line.
    /// </summary>
    private static List<Part> Dragon(double w, double h)
    {
        const double m = 8;                          // distance from the edge
        double rr = Math.Max(4, h / 2 - m);          // radius of the loop around each end
        double top = m, bottom = h - m, left = m + rr, right = w - m - rr;

        // Centre line, clockwise, sampled every ~2 px.
        var line = new List<Point>();
        void Straight(Point a, Point b)
        {
            int n = Math.Max(2, (int)(Dist(a, b) / 2));
            for (int i = 0; i < n; i++) { double t = (double)i / n; line.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t)); }
        }
        void Arc(Point c, double a0, double a1)
        {
            int n = Math.Max(4, (int)(Math.Abs(a1 - a0) * rr / 2));
            for (int i = 0; i < n; i++) { double a = a0 + (a1 - a0) * i / n; line.Add(new Point(c.X + rr * Math.Cos(a), c.Y + rr * Math.Sin(a))); }
        }
        Straight(new Point(w * 0.56, top), new Point(right, top));
        Arc(new Point(right, h / 2), -Math.PI / 2, Math.PI / 2);
        Straight(new Point(right, bottom), new Point(left, bottom));
        Arc(new Point(left, h / 2), Math.PI / 2, Math.PI * 1.5);
        line.Add(new Point(left + 2, top));

        // Arc length, tangents and outward normals (clockwise in y-down: outward = (t.y, -t.x)).
        var s = new List<double> { 0 };
        for (int i = 1; i < line.Count; i++) s.Add(s[i - 1] + Dist(line[i], line[i - 1]));
        double total = Math.Max(1, s[^1]);
        Point Tangent(int i)
        {
            Point a = line[Math.Max(0, i - 1)], b = line[Math.Min(line.Count - 1, i + 1)];
            double d = Math.Max(0.001, Dist(a, b));
            return new Point((b.X - a.X) / d, (b.Y - a.Y) / d);
        }

        // Waving centre and body width (thin tail, full body, slightly narrower neck).
        var centre = new List<Point>();
        var outward = new List<Point>();
        var width = new List<double>();
        for (int i = 0; i < line.Count; i++)
        {
            Point t = Tangent(i), n = new(t.Y, -t.X);
            double u = s[i] / total;
            double wave = 3 * Math.Sin(2 * Math.PI * s[i] / 58) * Math.Min(1, u * 4);
            centre.Add(new Point(line[i].X + n.X * wave, line[i].Y + n.Y * wave));
            outward.Add(n);
            width.Add(u < 0.3 ? 1 + 6 * u / 0.3 : (u > 0.9 ? 7 - 1.8 * (u - 0.9) / 0.1 : 7));
        }

        var body = Enumerable.Range(0, centre.Count).Select(i => Offset(centre[i], outward[i], width[i] / 2))
            .Concat(Enumerable.Range(0, centre.Count).Reverse().Select(i => Offset(centre[i], outward[i], -width[i] / 2))).ToList();

        // Spines on the back (outer side), scale marks across the body, a lighter belly line on the inner side.
        var spines = new List<List<Point>>();
        var marks = new List<List<Point>>();
        var belly = new List<Point>();
        double nextSpine = total * 0.12, nextMark = total * 0.06;
        for (int i = 0; i < centre.Count; i++)
        {
            if (s[i] >= total * 0.95) continue;
            if (s[i] > total * 0.1) belly.Add(Offset(centre[i], outward[i], -width[i] * 0.22));
            Point n = outward[i], t = new(-n.Y, n.X);
            double half = width[i] / 2;
            if (s[i] >= nextSpine)
            {
                nextSpine += 16;
                Point b = Offset(centre[i], n, half);
                double size = 2.5 + width[i] * 0.45;
                spines.Add(
                [
                    new Point(b.X - t.X * size * 0.6, b.Y - t.Y * size * 0.6),
                    new Point(b.X + n.X * size - t.X * size * 0.4, b.Y + n.Y * size - t.Y * size * 0.4),
                    new Point(b.X + t.X * size * 0.6, b.Y + t.Y * size * 0.6),
                ]);
            }
            if (s[i] >= nextMark && width[i] > 2.5)
            {
                nextMark += 8;
                marks.Add([Offset(centre[i], n, half * 0.8), new Point(centre[i].X + t.X * 1.6, centre[i].Y + t.Y * 1.6), Offset(centre[i], n, -half * 0.8)]);
            }
        }

        // Head at the end of the body, facing along the last tangent. Local frame: x forward, y outward (up).
        Point dir = Tangent(line.Count - 1), outN = outward[^1];
        var end = new Point(centre[^1].X - outN.X * 4.5, centre[^1].Y - outN.Y * 3); // a little inward: whole head visible, still above the text
        double angle = Math.Atan2(dir.Y, dir.X);
        const double k = 1.6;
        List<Point> Head(params (double X, double Y)[] pts) =>
            Transform(pts.Select(p => new Point(p.X * k, -p.Y * k)).ToList(), end, angle); // y flipped: outward is "up"

        var skull = Head((0, 2.6), (4, 5), (9, 6), (13, 5.2), (17, 3.8), (20.5, 2.2), (21.5, 0.6), (19, -0.4),
                         (14.5, -1), (19.5, -2.6), (16, -4.2), (10, -4.6), (5, -3.6), (0, -2.6));
        var horn = Head((8, 5.5), (4, 8.5), (-1, 10.5), (-6, 11), (-2, 9.6), (2, 7.6), (6, 5.4));
        var horn2 = Head((5.5, 5.6), (2, 7.4), (-2.5, 8), (1, 6.8), (4, 5.2));
        var mane = new List<List<Point>> { Head((1, 3), (-3, 6.5), (2, 4.4)), Head((-1, 1.8), (-5, 4.2), (0, 2.6)), Head((1, -2.6), (-3.5, -5.2), (1.5, -3.4)) };
        var whiskers = new List<List<Point>>
        {
            Head((19.5, -1.4), (23, -3), (27, -2.2), (30, -3.4), (31.5, -2)),
            Head((18.5, 3), (22.5, 5.6), (27, 4.8), (30.5, 6.4), (32, 5.2)),
        };
        var eye = Head(Circle(11.5, 3, 1.25));
        var pearl = Head(Circle(41, 0.5, 3.6));
        var flames = new List<List<Point>> { Head((36, 3.5), (34.5, 6.5), (37, 5.2)), Head((46, 3.6), (48, 6.2), (45.5, 5.4)), Head((41, 4.6), (41.5, 8)) };

        return
        [
            new Part([body], Role.Prompt, 0.55),
            new Part([.. spines, .. mane], Role.Highlight, 0.6),
            new Part(marks, Role.Glow, 0.4, 0.7),
            new Part([belly], Role.Highlight, 0.35, 1),
            new Part([skull, horn, horn2], Role.Prompt, 0.85),
            new Part([.. whiskers, .. flames], Role.Highlight, 0.8, 1.1),
            new Part([eye], Role.Glow, 1),
            new Part([pearl], Role.Highlight, 0.9, Glow: true),
        ];
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static double Dist(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static Point Offset(Point p, Point n, double d) => new(p.X + n.X * d, p.Y + n.Y * d);

    private static (double X, double Y)[] Circle(double cx, double cy, double r) =>
        Enumerable.Range(0, 16).Select(i => { double a = i / 16.0 * 2 * Math.PI; return (cx + r * Math.Cos(a), cy + r * Math.Sin(a)); }).ToArray();

    private static List<Point> Transform(List<Point> pts, Point basePoint, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return pts.Select(p => new Point(basePoint.X + p.X * c - p.Y * s, basePoint.Y + p.X * s + p.Y * c)).ToList();
    }
}
