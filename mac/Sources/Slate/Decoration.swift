import AppKit

/// Bar decorations drawn under the text. "vines": two winding branches with leaves along the top-right and
/// bottom-left edges (Forest theme). Geometry is computed in a y-down box of the bar's size.
/// Mirrors windows/src/Slate/Decoration.cs.
enum Decoration {
    struct Vine {
        var branch: [CGPoint] = []
        var leaves: [[CGPoint]] = []   // closed outlines, alternating two colours
    }

    static func vines(width w: CGFloat, height h: CGFloat, radius r: CGFloat) -> [Vine] {
        [
            vine(from: CGPoint(x: r * 0.5, y: h - 10), to: CGPoint(x: w * 0.4, y: h - 7), amplitude: 5, waves: 3, leaves: 15, seed: 1),
            vine(from: CGPoint(x: w - r * 0.5, y: 10), to: CGPoint(x: w * 0.56, y: 7), amplitude: 5, waves: 2.5, leaves: 13, seed: 2),
        ]
    }

    /// A gently waving branch from `a` to `b` with leaves alternating sides along it.
    private static func vine(from a: CGPoint, to b: CGPoint, amplitude: CGFloat, waves: CGFloat, leaves: Int, seed: Int) -> Vine {
        let dx = b.x - a.x, dy = b.y - a.y
        let length = max(1, hypot(dx, dy))
        let normal = CGPoint(x: -dy / length, y: dx / length)
        func point(_ t: CGFloat) -> CGPoint {
            let s = amplitude * sin(2 * .pi * waves * t) * (0.4 + 0.6 * t) // thinner wave near the root
            return CGPoint(x: a.x + dx * t + normal.x * s, y: a.y + dy * t + normal.y * s)
        }

        var vine = Vine()
        vine.branch = (0...60).map { point(CGFloat($0) / 60) }

        for i in 0..<leaves {
            let t = 0.1 + 0.85 * CGFloat(i) / CGFloat(max(1, leaves - 1))
            let p = point(t), q = point(min(1, t + 0.01))
            let along = atan2(q.y - p.y, q.x - p.x)
            let side: CGFloat = (i + seed) % 2 == 0 ? 1 : -1
            let angle = along + side * (.pi / 3.2)
            let size = 10 + 5 * CGFloat((i * 7 + seed * 3) % 5) / 4 // 10…15, varied but stable
            vine.leaves.append(leaf(at: p, angle: angle, length: size))
        }
        return vine
    }

    /// A pointed leaf: two arcs meeting at the tip, approximated with a polygon.
    private static func leaf(at base: CGPoint, angle: CGFloat, length: CGFloat) -> [CGPoint] {
        let width = length * 0.42
        var outline: [CGPoint] = []
        for i in 0...10 { // upper edge, base → tip
            let t = CGFloat(i) / 10
            outline.append(CGPoint(x: length * t, y: width * sin(.pi * t) * (1 - 0.3 * t)))
        }
        for i in (0...9).reversed() { // lower edge, tip → base
            let t = CGFloat(i) / 10
            outline.append(CGPoint(x: length * t, y: -width * 0.8 * sin(.pi * t)))
        }
        let c = cos(angle), s = sin(angle)
        return outline.map { CGPoint(x: base.x + $0.x * c - $0.y * s, y: base.y + $0.x * s + $0.y * c) }
    }
}
