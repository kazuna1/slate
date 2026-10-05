import AppKit

/// Bar decorations drawn under the text, as a list of filled or stroked shapes coloured from the theme.
///  - "vines": two winding branches with leaves along the top-right and bottom-left edges (Forest).
///  - "dunes": layered sand dunes, a hazy sun and wind streaks (Dune).
///  - "stars": a faint nebula, scattered stars and sparkles (Galaxy).
/// Geometry is computed in a y-down box of the bar's size. Mirrors windows/src/Slate/Decoration.cs.
enum Decoration {
    /// Which theme colour a shape uses.
    enum Role { case border, prompt, highlight, glow }

    struct Part {
        var paths: [[CGPoint]]
        var role: Role
        var alpha: CGFloat
        /// nil = filled (paths are closed outlines); a value = stroked with that width.
        var lineWidth: CGFloat?
        /// Soft glow around the shape (the pearl).
        var glow = false
    }

    static func parts(_ name: String, width w: CGFloat, height h: CGFloat, radius r: CGFloat) -> [Part] {
        switch name {
        case "vines": return vines(w, h, r)
        case "dunes": return dunes(w, h)
        case "stars": return stars(w, h)
        default: return []
        }
    }

    // MARK: Vines

    private static func vines(_ w: CGFloat, _ h: CGFloat, _ r: CGFloat) -> [Part] {
        let all = [
            vine(from: CGPoint(x: r * 0.5, y: h - 10), to: CGPoint(x: w * 0.4, y: h - 7), amplitude: 5, waves: 3, leaves: 15, seed: 1),
            vine(from: CGPoint(x: w - r * 0.5, y: 10), to: CGPoint(x: w * 0.56, y: 7), amplitude: 5, waves: 2.5, leaves: 13, seed: 2),
        ]
        let leaves = all.flatMap(\.leaves)
        return [
            Part(paths: all.map(\.branch), role: .border, alpha: 0.75, lineWidth: 1.8),
            Part(paths: leaves.enumerated().filter { $0.offset % 2 == 0 }.map(\.element), role: .prompt, alpha: 0.5),
            Part(paths: leaves.enumerated().filter { $0.offset % 2 == 1 }.map(\.element), role: .highlight, alpha: 0.32),
        ]
    }

    /// A gently waving branch from `a` to `b` with leaves alternating sides along it.
    private static func vine(from a: CGPoint, to b: CGPoint, amplitude: CGFloat, waves: CGFloat, leaves: Int, seed: Int)
        -> (branch: [CGPoint], leaves: [[CGPoint]]) {
        let dx = b.x - a.x, dy = b.y - a.y
        let length = max(1, hypot(dx, dy))
        let normal = CGPoint(x: -dy / length, y: dx / length)
        func point(_ t: CGFloat) -> CGPoint {
            let s = amplitude * sin(2 * .pi * waves * t) * (0.4 + 0.6 * t) // thinner wave near the root
            return CGPoint(x: a.x + dx * t + normal.x * s, y: a.y + dy * t + normal.y * s)
        }
        let branch = (0...60).map { point(CGFloat($0) / 60) }
        var list: [[CGPoint]] = []
        for i in 0..<leaves {
            let t = 0.1 + 0.85 * CGFloat(i) / CGFloat(max(1, leaves - 1))
            let p = point(t), q = point(min(1, t + 0.01))
            let along = atan2(q.y - p.y, q.x - p.x)
            let side: CGFloat = (i + seed) % 2 == 0 ? 1 : -1
            let size = 10 + 5 * CGFloat((i * 7 + seed * 3) % 5) / 4 // 10…15, varied but stable
            list.append(leaf(at: p, angle: along + side * (.pi / 3.2), length: size))
        }
        return (branch, list)
    }

    /// A pointed leaf: two arcs meeting at the tip, approximated with a polygon.
    private static func leaf(at base: CGPoint, angle: CGFloat, length: CGFloat) -> [CGPoint] {
        let width = length * 0.42
        var outline: [CGPoint] = []
        for i in 0...10 {
            let t = CGFloat(i) / 10
            outline.append(CGPoint(x: length * t, y: width * sin(.pi * t) * (1 - 0.3 * t)))
        }
        for i in (0...9).reversed() {
            let t = CGFloat(i) / 10
            outline.append(CGPoint(x: length * t, y: -width * 0.8 * sin(.pi * t)))
        }
        return transform(outline, at: base, angle: angle)
    }

    // MARK: Dunes

    /// Three layers of rolling sand dunes along the bottom, a hazy sun at the top-right, wind streaks top-left.
    private static func dunes(_ w: CGFloat, _ h: CGFloat) -> [Part] {
        func ridge(base: CGFloat, height: CGFloat, waves: CGFloat, phase: CGFloat) -> [CGPoint] {
            var pts = (0...80).map { i -> CGPoint in
                let x = w * CGFloat(i) / 80
                // Two summed waves: long gentle swells with a sharper crest, like real dunes.
                let y = base - height * (0.6 * sin(2 * .pi * waves * x / w + phase) + 0.4 * sin(2 * .pi * waves * 2.3 * x / w + phase * 1.7))
                return CGPoint(x: x, y: y)
            }
            pts.append(CGPoint(x: w, y: h + 2))
            pts.append(CGPoint(x: 0, y: h + 2))
            return pts
        }
        let sun = CGPoint(x: w * 0.6, y: h * 0.3)
        let streaks: [[CGPoint]] = [(0.34, 0.46, 0.13), (0.4, 0.55, 0.2), (0.3, 0.4, 0.24)].map { x0, x1, y in
            (0...20).map { i in
                let t = CGFloat(i) / 20
                return CGPoint(x: w * (x0 + (x1 - x0) * t), y: h * y + 1.2 * sin(t * .pi * 2))
            }
        }
        let front = ridge(base: h - 5, height: 3, waves: 3.1, phase: 4.0)
        return [
            Part(paths: [circlePoints(sun, 12)], role: .glow, alpha: 0.12),
            Part(paths: [circlePoints(sun, 7.5)], role: .prompt, alpha: 0.6, glow: true),
            Part(paths: [ridge(base: h - 15, height: 5, waves: 1.6, phase: 0.6)], role: .border, alpha: 0.32),
            Part(paths: [ridge(base: h - 10, height: 4.5, waves: 2.3, phase: 2.1)], role: .prompt, alpha: 0.42),
            Part(paths: [front], role: .glow, alpha: 0.6),
            Part(paths: [Array(front.dropLast(2))], role: .highlight, alpha: 0.4, lineWidth: 1),
            Part(paths: streaks, role: .highlight, alpha: 0.22, lineWidth: 1),
        ]
    }

    // MARK: Stars

    /// A faint nebula, scattered stars of different sizes and a few four-point sparkles. Seeded, so stable.
    private static func stars(_ w: CGFloat, _ h: CGFloat) -> [Part] {
        var seed: UInt64 = 0x51A7E
        func random() -> CGFloat {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            return CGFloat((seed >> 33) % 10_000) / 10_000
        }
        // Soft clouds: stacked ellipses shrinking toward the centre, so they fade out at the edges.
        func cloud(_ fx: CGFloat, _ fy: CGFloat, _ rx: CGFloat, _ ry: CGFloat) -> [[CGPoint]] {
            (0..<7).map { i in let k = 1 - CGFloat(i) * 0.12; return ellipse(CGPoint(x: w * fx, y: h * fy), w * rx * k, h * ry * k) }
        }
        let nebula = cloud(0.62, 0.5, 0.2, 0.55) + cloud(0.25, 0.62, 0.09, 0.45)
        let core = cloud(0.66, 0.42, 0.08, 0.3)
        var small: [[CGPoint]] = [], bright: [[CGPoint]] = []
        for _ in 0..<70 {
            let p = CGPoint(x: random() * w, y: random() * h), r = 0.35 + random() * 0.8
            if r > 0.95 { bright.append(circlePoints(p, r, segments: 8)) } else { small.append(circlePoints(p, r, segments: 8)) }
        }
        let sparkles: [[CGPoint]] = [(0.07, 0.28), (0.47, 0.18), (0.9, 0.74), (0.33, 0.82)].map { fx, fy in
            let c = CGPoint(x: w * fx, y: h * fy), big: CGFloat = 4.5, thin: CGFloat = 0.9
            return [CGPoint(x: c.x, y: c.y - big), CGPoint(x: c.x + thin, y: c.y - thin), CGPoint(x: c.x + big, y: c.y),
                    CGPoint(x: c.x + thin, y: c.y + thin), CGPoint(x: c.x, y: c.y + big), CGPoint(x: c.x - thin, y: c.y + thin),
                    CGPoint(x: c.x - big, y: c.y), CGPoint(x: c.x - thin, y: c.y - thin)]
        }
        return [
            Part(paths: nebula, role: .glow, alpha: 0.045),
            Part(paths: core, role: .prompt, alpha: 0.04),
            Part(paths: small, role: .highlight, alpha: 0.55),
            Part(paths: bright, role: .highlight, alpha: 0.9, glow: true),
            Part(paths: sparkles, role: .highlight, alpha: 0.85, glow: true),
        ]
    }

    // MARK: Helpers

    private static func circlePoints(_ c: CGPoint, _ r: CGFloat, segments: Int = 24) -> [CGPoint] {
        (0..<segments).map { let a = CGFloat($0) / CGFloat(segments) * 2 * .pi; return CGPoint(x: c.x + r * cos(a), y: c.y + r * sin(a)) }
    }

    private static func ellipse(_ c: CGPoint, _ rx: CGFloat, _ ry: CGFloat) -> [CGPoint] {
        (0..<36).map { let a = CGFloat($0) / 36 * 2 * .pi; return CGPoint(x: c.x + rx * cos(a), y: c.y + ry * sin(a)) }
    }



    private static func transform(_ pts: [CGPoint], at base: CGPoint, angle: CGFloat) -> [CGPoint] {
        let c = cos(angle), s = sin(angle)
        return pts.map { CGPoint(x: base.x + $0.x * c - $0.y * s, y: base.y + $0.x * s + $0.y * c) }
    }
}
