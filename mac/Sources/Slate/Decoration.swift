import AppKit

/// Bar decorations drawn under the text, as a list of filled or stroked shapes coloured from the theme.
///  - "vines": two winding branches with leaves along the top-right and bottom-left edges (Forest).
///  - "dragon": a Chinese dragon spiralling around the bar, head at the top-left chasing a pearl (Dragon).
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
        case "dragon": return dragon(w, h)
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

    // MARK: Dragon

    /// The body runs clockwise around the bar (tail mid-top → right end → bottom → left end → head at
    /// the top-left facing right), waving, tapering, with spines on its back and scale marks across it.
    private static func dragon(_ w: CGFloat, _ h: CGFloat) -> [Part] {
        let m: CGFloat = 8                       // distance from the edge
        let rr = max(4, h / 2 - m)               // radius of the loop around each end
        let top = m, bottom = h - m, left = m + rr, right = w - m - rr

        // Centre line, clockwise, sampled every ~2 px.
        var line: [CGPoint] = []
        func straight(_ a: CGPoint, _ b: CGPoint) {
            let n = max(2, Int(hypot(b.x - a.x, b.y - a.y) / 2))
            for i in 0..<n { let t = CGFloat(i) / CGFloat(n); line.append(CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t)) }
        }
        func arc(center c: CGPoint, from a0: CGFloat, to a1: CGFloat) {
            let n = max(4, Int(abs(a1 - a0) * rr / 2))
            for i in 0..<n { let a = a0 + (a1 - a0) * CGFloat(i) / CGFloat(n); line.append(CGPoint(x: c.x + rr * cos(a), y: c.y + rr * sin(a))) }
        }
        straight(CGPoint(x: w * 0.56, y: top), CGPoint(x: right, y: top))
        arc(center: CGPoint(x: right, y: h / 2), from: -.pi / 2, to: .pi / 2)
        straight(CGPoint(x: right, y: bottom), CGPoint(x: left, y: bottom))
        arc(center: CGPoint(x: left, y: h / 2), from: .pi / 2, to: .pi * 1.5)
        line.append(CGPoint(x: left + 2, y: top))

        // Arc length, tangents and outward normals (clockwise in y-down: outward = (t.y, -t.x)).
        var s: [CGFloat] = [0]
        for i in 1..<line.count { s.append(s[i - 1] + hypot(line[i].x - line[i - 1].x, line[i].y - line[i - 1].y)) }
        let total = max(1, s.last!)
        func tangent(_ i: Int) -> CGPoint {
            let a = line[max(0, i - 1)], b = line[min(line.count - 1, i + 1)]
            let d = max(0.001, hypot(b.x - a.x, b.y - a.y))
            return CGPoint(x: (b.x - a.x) / d, y: (b.y - a.y) / d)
        }
        // Waving centre and body width (thin tail, full body, slightly narrower neck).
        var centre: [CGPoint] = [], outward: [CGPoint] = [], width: [CGFloat] = []
        for i in line.indices {
            let t = tangent(i), n = CGPoint(x: t.y, y: -t.x), u = s[i] / total
            let wave = 3 * sin(2 * .pi * s[i] / 58) * min(1, u * 4)
            centre.append(CGPoint(x: line[i].x + n.x * wave, y: line[i].y + n.y * wave))
            outward.append(n)
            width.append(u < 0.3 ? 1 + 6 * u / 0.3 : (u > 0.9 ? 7 - 1.8 * (u - 0.9) / 0.1 : 7))
        }

        let body = centre.indices.map { CGPoint(x: centre[$0].x + outward[$0].x * width[$0] / 2, y: centre[$0].y + outward[$0].y * width[$0] / 2) }
            + centre.indices.reversed().map { CGPoint(x: centre[$0].x - outward[$0].x * width[$0] / 2, y: centre[$0].y - outward[$0].y * width[$0] / 2) }

        // Spines on the back (outer side), scale marks across the body, a lighter belly line on the inner side.
        var spines: [[CGPoint]] = [], marks: [[CGPoint]] = [], belly: [CGPoint] = []
        var nextSpine: CGFloat = total * 0.12, nextMark: CGFloat = total * 0.06
        for i in centre.indices where s[i] < total * 0.95 {
            if s[i] > total * 0.1 {
                belly.append(CGPoint(x: centre[i].x - outward[i].x * width[i] * 0.22, y: centre[i].y - outward[i].y * width[i] * 0.22))
            }
            let n = outward[i], t = CGPoint(x: -n.y, y: n.x), half = width[i] / 2
            if s[i] >= nextSpine {
                nextSpine += 16
                let base = CGPoint(x: centre[i].x + n.x * half, y: centre[i].y + n.y * half)
                let size = 2.5 + width[i] * 0.45
                spines.append([
                    CGPoint(x: base.x - t.x * size * 0.6, y: base.y - t.y * size * 0.6),
                    CGPoint(x: base.x + n.x * size - t.x * size * 0.4, y: base.y + n.y * size - t.y * size * 0.4),
                    CGPoint(x: base.x + t.x * size * 0.6, y: base.y + t.y * size * 0.6),
                ])
            }
            if s[i] >= nextMark, width[i] > 2.5 {
                nextMark += 8
                let a = CGPoint(x: centre[i].x + n.x * half * 0.8, y: centre[i].y + n.y * half * 0.8)
                let b = CGPoint(x: centre[i].x - n.x * half * 0.8, y: centre[i].y - n.y * half * 0.8)
                let bend = CGPoint(x: centre[i].x + t.x * 1.6, y: centre[i].y + t.y * 1.6)
                marks.append([a, bend, b])
            }
        }

        // Head at the end of the body, facing along the last tangent. Local frame: x forward, y outward (up).
        let dir = tangent(line.count - 1), out = outward[line.count - 1]
        let end = CGPoint(x: centre.last!.x - out.x * 4.5, y: centre.last!.y - out.y * 3) // a little inward: whole head visible, still above the text
        let angle = atan2(dir.y, dir.x)
        let k: CGFloat = 1.6
        func head(_ pts: [(CGFloat, CGFloat)]) -> [CGPoint] {
            transform(pts.map { CGPoint(x: $0.0 * k, y: -$0.1 * k) }, at: end, angle: angle) // y flipped: outward is "up"
        }
        let skull = head([(0, 2.6), (4, 5), (9, 6), (13, 5.2), (17, 3.8), (20.5, 2.2), (21.5, 0.6), (19, -0.4),
                          (14.5, -1), (19.5, -2.6), (16, -4.2), (10, -4.6), (5, -3.6), (0, -2.6)])
        let horn = head([(8, 5.5), (4, 8.5), (-1, 10.5), (-6, 11), (-2, 9.6), (2, 7.6), (6, 5.4)])
        let horn2 = head([(5.5, 5.6), (2, 7.4), (-2.5, 8), (1, 6.8), (4, 5.2)])
        let mane = [head([(1, 3), (-3, 6.5), (2, 4.4)]), head([(-1, 1.8), (-5, 4.2), (0, 2.6)]),
                    head([(1, -2.6), (-3.5, -5.2), (1.5, -3.4)])]
        let whiskers = [
            head([(19.5, -1.4), (23, -3), (27, -2.2), (30, -3.4), (31.5, -2)]),
            head([(18.5, 3), (22.5, 5.6), (27, 4.8), (30.5, 6.4), (32, 5.2)]),
        ]
        let eye = head(circle(CGPoint(x: 11.5, y: 3), 1.25))
        let pearl = head(circle(CGPoint(x: 41, y: 0.5), 3.6))
        let flames = [head([(36, 3.5), (34.5, 6.5), (37, 5.2)]), head([(46, 3.6), (48, 6.2), (45.5, 5.4)]),
                      head([(41, 4.6), (41.5, 8)])]

        return [
            Part(paths: [body], role: .prompt, alpha: 0.55),
            Part(paths: spines + mane, role: .highlight, alpha: 0.6),
            Part(paths: marks, role: .glow, alpha: 0.4, lineWidth: 0.7),
            Part(paths: [belly], role: .highlight, alpha: 0.35, lineWidth: 1),
            Part(paths: [skull, horn, horn2], role: .prompt, alpha: 0.85),
            Part(paths: whiskers + flames, role: .highlight, alpha: 0.8, lineWidth: 1.1),
            Part(paths: [eye], role: .glow, alpha: 1),
            Part(paths: [pearl], role: .highlight, alpha: 0.9, glow: true),
        ]
    }

    // MARK: Helpers

    private static func circle(_ c: CGPoint, _ r: CGFloat) -> [(CGFloat, CGFloat)] {
        (0..<16).map { let a = CGFloat($0) / 16 * 2 * .pi; return (c.x + r * cos(a), c.y + r * sin(a)) }
    }

    private static func transform(_ pts: [CGPoint], at base: CGPoint, angle: CGFloat) -> [CGPoint] {
        let c = cos(angle), s = sin(angle)
        return pts.map { CGPoint(x: base.x + $0.x * c - $0.y * s, y: base.y + $0.x * s + $0.y * c) }
    }
}
