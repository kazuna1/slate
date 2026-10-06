import Foundation

/// Finds folders by name, so "cc slate" works on any Mac with no setup and no zoxide.
///
/// Known folders, ranked first: ones you've opened (through Slate, or in any terminal when "learn from
/// terminal" is on), ranked by frecency (how often and how recently); zoxide's list if installed;
/// subfolders of the default folder and `projectRoots`; git repos and their parent folders.
/// Then every other folder in your home folder (an index rebuilt every few hours), and finally
/// Spotlight, which knows a folder the moment it's created. Mirrors windows/src/Slate/Projects.cs.
final class Projects {
    private static let indexURL = Paths.appSupport.appendingPathComponent("projects.json")
    private static let rescanAfter: TimeInterval = 6 * 3600
    private static let skipDirs: Set<String> = [
        "node_modules", "bin", "obj", "dist", "build", "target", "Library", "Applications", "Pictures", "Movies",
        "Music", "Public", "venv", "__pycache__", "Pods", "DerivedData",
    ]
    /// macOS privacy-protected folders: reading them from the background scan makes macOS ask for permission
    /// ("Slate would like to access files in your Documents folder"). Projects in them are still found by
    /// name through Spotlight when you ask for one.
    private static let protectedDirs: Set<String> = ["Desktop", "Documents", "Downloads"]

    private struct Visit: Codable {
        var count: Double
        var last: Date
    }

    private struct Index: Codable {
        var scannedAt = Date.distantPast
        var repos: [String] = []
        /// Every folder in the home folder (not inside repos, packages or junk folders).
        var folders: [String] = []
        /// Folder → how often and when it was last opened.
        var visits: [String: Visit] = [:]

        init() {}

        // Older versions stored "used": [path: count]; missing keys get defaults.
        init(from decoder: Decoder) throws {
            let c = try decoder.container(keyedBy: Keys.self)
            scannedAt = try c.decodeIfPresent(Date.self, forKey: .scannedAt) ?? .distantPast
            repos = try c.decodeIfPresent([String].self, forKey: .repos) ?? []
            folders = try c.decodeIfPresent([String].self, forKey: .folders) ?? []
            visits = try c.decodeIfPresent([String: Visit].self, forKey: .visits) ?? [:]
            for (path, count) in try c.decodeIfPresent([String: Int].self, forKey: .used) ?? [:] where visits[path] == nil {
                visits[path] = Visit(count: Double(count), last: Date().addingTimeInterval(-86400))
            }
            if folders.isEmpty { scannedAt = .distantPast } // pre-1.6 index: scan now
        }

        func encode(to encoder: Encoder) throws {
            var c = encoder.container(keyedBy: Keys.self)
            try c.encode(scannedAt, forKey: .scannedAt)
            try c.encode(repos, forKey: .repos)
            try c.encode(folders, forKey: .folders)
            try c.encode(visits, forKey: .visits)
        }

        private enum Keys: String, CodingKey { case scannedAt, repos, folders, visits, used }
    }

    private let lock = NSLock()
    private var index: Index
    private var scanning = false

    init() {
        index = (try? JSONDecoder().decode(Index.self, from: Data(contentsOf: Self.indexURL))) ?? Index()
    }

    // MARK: Scanning

    /// Kicks off a background rescan if the cached one is stale.
    func refreshIfStale() {
        ingestTerminalVisits()
        lock.lock()
        defer { lock.unlock() }
        guard !scanning, Date().timeIntervalSince(index.scannedAt) > Self.rescanAfter else { return }
        scanning = true
        DispatchQueue.global(qos: .utility).async {
            var repos: [String] = []
            var folders: [String] = []
            Self.walk(URL(fileURLWithPath: NSHomeDirectory()), depth: 6, repos: &repos, folders: &folders, top: true)
            // Shallow paths first, sorted once here instead of on every lookup.
            folders = folders.map { ($0, $0.split(separator: "/").count) }.sorted { $0.1 < $1.1 }.map(\.0)
            self.lock.lock()
            self.index.repos = repos
            self.index.folders = folders
            self.index.scannedAt = Date()
            self.scanning = false
            self.save()
            self.lock.unlock()
        }
    }

    private static func walk(_ dir: URL, depth: Int, repos: inout [String], folders: inout [String], top: Bool = false) {
        guard depth >= 0, folders.count < 200_000 else { return }
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .isPackageKey]
        let items = (try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: keys, options: [.skipsHiddenFiles, .skipsPackageDescendants])) ?? []
        for item in items {
            let values = try? item.resourceValues(forKeys: Set(keys))
            guard values?.isDirectory == true, values?.isSymbolicLink != true, values?.isPackage != true,
                  !skipDirs.contains(item.lastPathComponent),
                  !(top && protectedDirs.contains(item.lastPathComponent)) else { continue }
            folders.append(item.path)
            if FileManager.default.fileExists(atPath: item.appendingPathComponent(".git").path) {
                repos.append(item.path) // a project: don't index its insides
                continue
            }
            walk(item, depth: depth - 1, repos: &repos, folders: &folders)
        }
    }

    // MARK: Lookup

    /// Best folder for a query like "slate" or "new airlink", or nil.
    /// The last resort asks Spotlight, which takes a few milliseconds.
    func resolve(_ raw: String, config: SlateConfig) -> String? {
        let query = raw.trimmingCharacters(in: CharacterSet(charactersIn: " '\""))
        guard !query.isEmpty else { return nil }

        let path = expandPath(query)
        if path.hasPrefix("/"), Self.isDirectory(path) { return path }

        if config.useZoxide, let z = Zoxide.query(query), Self.isDirectory(z) { return z }

        // Known folders first, then everything in the index (already shallow-first).
        lock.lock()
        let indexed = index.folders
        lock.unlock()
        if let found = Self.rank(candidates(config) + indexed, query).first(where: Self.isDirectory) { return found }

        // A folder created after the last scan: Spotlight already knows it.
        return Self.spotlight(query)
    }

    /// The shallowest folder named exactly `name` in the home folder, via Spotlight (always current).
    private static func spotlight(_ name: String) -> String? {
        let escaped = name.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/mdfind")
        p.arguments = ["-onlyin", NSHomeDirectory(),
                       "kMDItemFSName == \"\(escaped)\"c && kMDItemContentType == \"public.folder\""]
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
            .filter { !$0.contains("/Library/") && !$0.contains("/.") && isDirectory($0) }
            .min { $0.split(separator: "/").count < $1.split(separator: "/").count }
    }

    /// Remembers a folder that was opened, so it ranks higher next time (and tells zoxide too).
    func visited(_ folder: String, config: SlateConfig) {
        lock.lock()
        bump(folder, at: Date())
        save()
        lock.unlock()
        if config.useZoxide { Zoxide.add(folder) }
    }

    /// Caller holds the lock.
    private func bump(_ folder: String, at when: Date) {
        var v = index.visits[folder] ?? Visit(count: 0, last: .distantPast)
        v.count += 1
        if when > v.last { v.last = when }
        index.visits[folder] = v
    }

    /// Known folders, best first. Feeds Tab completion and bare project names ("slate").
    func all(_ config: SlateConfig) -> [String] { candidates(config) }

    private func candidates(_ config: SlateConfig) -> [String] {
        lock.lock()
        let now = Date()
        let used = index.visits.sorted { Self.frecency($0.value, now) > Self.frecency($1.value, now) }.map(\.key)
        let repos = index.repos
        lock.unlock()

        var result = used
        if config.useZoxide { result += Zoxide.list() }
        for root in config.projectRoots { result += Self.subfolders(of: root) }
        // Projects in the default folder count too (not when it's home: that's Desktop, Documents, ...).
        let base = CommandRunner.workingDirectory(config.workingDirectory)
        if base != NSHomeDirectory() { result += Self.subfolders(of: base) }
        result += repos
        // Folders that hold repos ("new airlink" holding "ndc") are often what people type.
        result += repos.map { ($0 as NSString).deletingLastPathComponent }.filter { $0 != NSHomeDirectory() && $0 != "/" }

        var seen = Set<String>()
        return result.filter { seen.insert($0).inserted && Self.isDirectory($0) }
    }

    /// zoxide-style score: visit count weighted by how recent the last visit was.
    private static func frecency(_ v: Visit, _ now: Date) -> Double {
        let age = now.timeIntervalSince(v.last)
        let weight = age < 3600 ? 4.0 : age < 86400 ? 2.0 : age < 7 * 86400 ? 0.5 : 0.25
        return v.count * weight
    }

    /// Exact name, then prefix, then contains; keeps source order (= rank) within each group.
    private static func rank(_ folders: [String], _ query: String) -> [String] {
        let q = query.lowercased()
        func name(_ p: String) -> String { (p as NSString).lastPathComponent.lowercased() }
        var seen = Set<String>()
        return (folders.filter { name($0) == q } + folders.filter { name($0).hasPrefix(q) } + folders.filter { name($0).contains(q) })
            .filter { seen.insert($0).inserted }
    }

    private static func subfolders(of root: String) -> [String] {
        let dir = URL(fileURLWithPath: expandPath(root), isDirectory: true)
        let items = (try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        return items.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true }.map(\.path)
    }

    private static func isDirectory(_ path: String) -> Bool {
        var isDir: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDir) && isDir.boolValue
    }

    // MARK: Learning from terminals (ShellHook writes visits.log; we fold it in)

    private func ingestTerminalVisits() {
        let log = ShellHook.visitsLog
        let taken = log.appendingPathExtension("reading")
        guard FileManager.default.fileExists(atPath: log.path) else { return }
        try? FileManager.default.removeItem(at: taken)
        guard (try? FileManager.default.moveItem(at: log, to: taken)) != nil,
              let text = try? String(contentsOf: taken, encoding: .utf8) else { return }
        try? FileManager.default.removeItem(at: taken)
        lock.lock()
        for line in text.split(separator: "\n") {
            let parts = line.split(separator: "\t", maxSplits: 1)
            guard parts.count == 2, let unix = Double(parts[0]) else { continue }
            let path = String(parts[1])
            if path.count > 1, Self.isDirectory(path) { bump(path, at: Date(timeIntervalSince1970: unix)) }
        }
        save()
        lock.unlock()
    }

    /// Caller holds the lock.
    private func save() {
        do { try JSONEncoder().encode(index).write(to: Self.indexURL, options: .atomic) } catch { Log.error("Saving project index", error) }
    }
}

/// Optional zoxide integration; everything returns empty when zoxide isn't installed.
enum Zoxide {
    /// GUI apps get a minimal PATH, so look where package managers put zoxide.
    private static let paths = ["/opt/homebrew/bin/zoxide", "/usr/local/bin/zoxide",
                                NSHomeDirectory() + "/.local/bin/zoxide", NSHomeDirectory() + "/.cargo/bin/zoxide"]

    static func query(_ query: String) -> String? {
        let words = query.split(separator: " ").map(String.init)
        let out = run(["query", "--"] + words)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return out.isEmpty ? nil : out
    }

    static func list() -> [String] {
        run(["query", "-l"])?.split(separator: "\n").map(String.init) ?? []
    }

    static func add(_ folder: String) {
        DispatchQueue.global(qos: .utility).async { _ = run(["add", "--", folder]) }
    }

    private static func run(_ args: [String]) -> String? {
        guard let exe = paths.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return nil }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return p.terminationStatus == 0 ? String(decoding: data, as: UTF8.self) : nil
    }
}
