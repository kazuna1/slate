import Foundation

/// Folder-name completion for command arguments (e.g. "code sl" → "code slate").
/// Candidates come from zoxide's ranked database plus subfolders of the configured project roots.
final class Completer {
    private var names: [String] = []
    private var refreshing = false

    /// GUI apps get a minimal PATH, so look where package managers put zoxide.
    private static let zoxidePaths = ["/opt/homebrew/bin/zoxide", "/usr/local/bin/zoxide",
                                      NSHomeDirectory() + "/.local/bin/zoxide", NSHomeDirectory() + "/.cargo/bin/zoxide"]

    /// Rebuilds the candidate list in the background; cheap enough to call on every summon.
    func refresh(_ config: SlateConfig) {
        guard !refreshing else { return }
        refreshing = true
        let useZoxide = config.useZoxide
        let roots = config.projectRoots

        DispatchQueue.global(qos: .userInitiated).async {
            var paths: [String] = []
            if useZoxide { paths += Self.queryZoxide() }
            for root in roots { paths += Self.subfolders(of: root) }

            // Keep the first (highest-ranked) occurrence of each folder name.
            var seen = Set<String>()
            var result: [String] = []
            for path in paths {
                let name = (path as NSString).lastPathComponent
                if !name.isEmpty, !name.hasPrefix("."), seen.insert(name.lowercased()).inserted { result.append(name) }
            }
            DispatchQueue.main.async {
                self.names = result
                self.refreshing = false
            }
        }
    }

    /// Prefix matches first (in rank order), then substring matches.
    func match(_ prefix: String) -> [String] {
        guard !prefix.isEmpty else { return names }
        let p = prefix.lowercased()
        let starts = names.filter { $0.lowercased().hasPrefix(p) }
        let contains = names.filter { !$0.lowercased().hasPrefix(p) && $0.lowercased().contains(p) }
        return starts + contains
    }

    private static func queryZoxide() -> [String] {
        guard let exe = zoxidePaths.first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return [] }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = ["query", "-l"]
        let pipe = Pipe()
        p.standardOutput = pipe
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return [] }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
    }

    private static func subfolders(of root: String) -> [String] {
        let dir = URL(fileURLWithPath: expandPath(root), isDirectory: true)
        let items = (try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? []
        return items.filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true }.map(\.path)
    }
}
