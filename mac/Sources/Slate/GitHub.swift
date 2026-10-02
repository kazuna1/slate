import Foundation

/// Every GitHub repo your `gh` login can access (your own, other accounts' you collaborate on, orgs),
/// so "git clone ladder" works without a URL. Cached in repos.json, refreshed in the background.
/// Mirrors windows/src/Slate/GitHub.cs.
final class GitHubRepos {
    private static let cacheURL = Paths.appSupport.appendingPathComponent("repos.json")
    private static let refreshAfter: TimeInterval = 3600
    private static let ghPaths = ["/opt/homebrew/bin/gh", "/usr/local/bin/gh", NSHomeDirectory() + "/.local/bin/gh"]

    private struct Cache: Codable {
        var fetchedAt = Date.distantPast
        var repos: [String] = [] // "owner/name"
    }

    private let lock = NSLock()
    private var cache: Cache
    private var fetching = false

    static var ghPath: String? { ghPaths.first { FileManager.default.isExecutableFile(atPath: $0) } }

    init() {
        cache = (try? JSONDecoder().decode(Cache.self, from: Data(contentsOf: Self.cacheURL))) ?? Cache()
    }

    var all: [String] {
        lock.lock(); defer { lock.unlock() }
        return cache.repos
    }

    /// Refreshes the list in the background if it's older than an hour.
    func refreshIfStale() {
        lock.lock()
        let stale = !fetching && Date().timeIntervalSince(cache.fetchedAt) > Self.refreshAfter
        if stale { fetching = true }
        lock.unlock()
        guard stale else { return }
        DispatchQueue.global(qos: .utility).async { _ = try? self.fetch() }
    }

    /// Repos matching a name ("ladder") or "owner/name". Exact names win over prefixes.
    /// Fetches the list first if it has never been loaded (call off the main thread).
    func find(_ query: String) throws -> [String] {
        let q = query.lowercased().trimmingCharacters(in: .whitespaces)
        if q.contains("/") { return [query] } // owner/name: gh clones it directly
        if all.isEmpty { try fetch() }
        let repos = all
        func name(_ r: String) -> String { String(r.split(separator: "/").last ?? "").lowercased() }
        let exact = repos.filter { name($0) == q }
        return exact.isEmpty ? repos.filter { name($0).hasPrefix(q) } : exact
    }

    /// Repo names (and owner/name for the ambiguous ones) for Tab completion.
    func completions(_ prefix: String) -> [String] {
        let p = prefix.lowercased()
        let repos = all
        var names: [String] = []
        var seen = Set<String>()
        for repo in repos {
            let name = String(repo.split(separator: "/").last ?? "")
            let key = p.contains("/") ? repo : name
            if key.lowercased().hasPrefix(p), seen.insert(key.lowercased()).inserted { names.append(key) }
        }
        return names
    }

    @discardableResult
    private func fetch() throws -> [String] {
        defer { lock.lock(); fetching = false; lock.unlock() }
        guard let gh = Self.ghPath else {
            throw SlateError("GitHub CLI (gh) isn't installed. Install it with: brew install gh")
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: gh)
        p.arguments = ["api", "user/repos?per_page=100&affiliation=owner,collaborator,organization_member",
                       "--paginate", "--jq", ".[].full_name"]
        let out = Pipe(), err = Pipe()
        p.standardOutput = out
        p.standardError = err
        try p.run()
        let data = out.fileHandleForReading.readDataToEndOfFile()
        let errData = err.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else {
            let message = String(decoding: errData, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            Log.write("gh api user/repos failed: \(message)")
            throw SlateError(message.contains("auth") || message.contains("login")
                             ? "GitHub CLI isn't logged in. Run: gh auth login" : "Couldn't list your GitHub repos: \(message)")
        }
        let repos = String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init)
        lock.lock()
        cache = Cache(fetchedAt: Date(), repos: repos)
        try? JSONEncoder().encode(cache).write(to: Self.cacheURL, options: .atomic)
        lock.unlock()
        return repos
    }
}
