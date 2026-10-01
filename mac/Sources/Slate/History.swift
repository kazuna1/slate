import Foundation

/// Command history with shell-style ↑/↓ navigation, persisted to disk.
final class History {
    private var items: [String]
    private var index = -1 // -1 = not navigating
    private var draft = ""

    var maxSize: Int

    init(maxSize: Int) {
        self.maxSize = max(1, maxSize)
        let text = (try? String(contentsOf: Paths.history, encoding: .utf8)) ?? ""
        items = text.split(separator: "\n").map(String.init)
    }

    func add(_ command: String) {
        items.removeAll { $0 == command }
        items.append(command)
        if items.count > maxSize { items.removeFirst(items.count - maxSize) }
        resetNavigation()
        save()
    }

    /// Older entry, or nil if there is none.
    func previous(current: String) -> String? {
        guard !items.isEmpty else { return nil }
        if index == -1 {
            draft = current
            index = items.count
        }
        if index > 0 { index -= 1 }
        return items[index]
    }

    /// Newer entry; past the newest returns the original draft. Nil if not navigating.
    func next() -> String? {
        guard index != -1 else { return nil }
        index += 1
        if index < items.count { return items[index] }
        index = -1
        return draft
    }

    func resetNavigation() { index = -1 }

    func clear() {
        items.removeAll()
        resetNavigation()
        save()
    }

    private func save() {
        do {
            try items.joined(separator: "\n").write(to: Paths.history, atomically: true, encoding: .utf8)
        } catch {
            Log.error("Saving history", error)
        }
    }
}
