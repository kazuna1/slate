import Foundation

/// Folder-name completion for command arguments (e.g. "code sl" → "code slate").
/// Candidates come from `Projects`: folders you use, zoxide, project roots and discovered repos.
final class Completer {
    private let projects: Projects
    private var names: [String] = []
    private var refreshing = false

    init(projects: Projects) { self.projects = projects }

    /// Rebuilds the candidate list in the background; cheap enough to call on every summon.
    func refresh(_ config: SlateConfig) {
        guard !refreshing else { return }
        refreshing = true
        projects.refreshIfStale()

        DispatchQueue.global(qos: .userInitiated).async {
            // Keep the first (highest-ranked) occurrence of each folder name.
            var seen = Set<String>()
            var result: [String] = []
            for path in self.projects.all(config) {
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
}
