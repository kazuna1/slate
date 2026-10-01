import AppKit
import CryptoKit

struct UpdateInfo {
    let version: String
    let downloadURL: URL
    let sha256: String?
    let pageURL: URL
}

/// Checks GitHub for a newer release and swaps the app bundle in place.
enum Updater {
    private static let latestReleaseAPI = URL(string: "https://api.github.com/repos/kazuna1/slate/releases/latest")!
    static let assetName = "Slate-macOS.zip"

    static var currentVersion: String {
        Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "0.0.0"
    }

    /// The running Slate.app, or nil when run outside a bundle (swift run).
    static var appBundle: URL? {
        Bundle.main.bundleURL.pathExtension == "app" ? Bundle.main.bundleURL : nil
    }

    /// The newer release, or nil if this is the latest.
    static func check() async throws -> UpdateInfo? {
        var request = URLRequest(url: latestReleaseAPI)
        request.setValue("Slate/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        let (data, _) = try await URLSession.shared.data(for: request)
        guard let release = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = release["tag_name"] as? String,
              let page = (release["html_url"] as? String).flatMap(URL.init(string:)),
              let assets = release["assets"] as? [[String: Any]] else {
            throw SlateError("Unexpected response from GitHub.")
        }
        let latest = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
        guard isNewer(latest, than: currentVersion) else { return nil }

        guard let asset = assets.first(where: { $0["name"] as? String == assetName }),
              let url = (asset["browser_download_url"] as? String).flatMap(URL.init(string:)) else {
            return nil // release has no macOS build (yet)
        }
        // GitHub publishes "sha256:<hex>" for each asset; verify against it when present.
        let digest = (asset["digest"] as? String).flatMap { $0.hasPrefix("sha256:") ? String($0.dropFirst(7)) : nil }
        return UpdateInfo(version: latest, downloadURL: url, sha256: digest, pageURL: page)
    }

    /// Downloads, verifies and unpacks the update, then hands off to a helper that waits for
    /// Slate to quit, swaps the bundle and relaunches it. The caller should quit right after.
    static func install(_ update: UpdateInfo) async throws {
        guard let app = appBundle else { throw SlateError("Slate isn't running from an app bundle.") }

        let (downloaded, response) = try await URLSession.shared.download(from: update.downloadURL)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw SlateError("Download failed.") }

        if let expected = update.sha256 {
            let actual = SHA256.hash(data: try Data(contentsOf: downloaded)).map { String(format: "%02x", $0) }.joined()
            guard actual.caseInsensitiveCompare(expected) == .orderedSame else {
                try? FileManager.default.removeItem(at: downloaded)
                throw SlateError("The download didn't match its checksum, so it wasn't installed.")
            }
        }

        let staging = FileManager.default.temporaryDirectory.appendingPathComponent("slate-update-\(update.version)")
        try? FileManager.default.removeItem(at: staging)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        try run("/usr/bin/ditto", ["-x", "-k", downloaded.path, staging.path])
        let newApp = staging.appendingPathComponent("Slate.app")
        guard FileManager.default.fileExists(atPath: newApp.path) else { throw SlateError("The update didn't contain Slate.app.") }

        let swap = """
        while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done
        APP=\(shellQuote(app.path)); NEW=\(shellQuote(newApp.path))
        rm -rf "$APP.old"
        if mv "$APP" "$APP.old" && /usr/bin/ditto "$NEW" "$APP"; then rm -rf "$APP.old"; else rm -rf "$APP"; mv "$APP.old" "$APP"; fi
        rm -rf \(shellQuote(staging.path))
        /usr/bin/open "$APP"
        """
        let helper = Process()
        helper.executableURL = URL(fileURLWithPath: "/bin/sh")
        helper.arguments = ["-c", swap]
        try helper.run()
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            let p = s.split(separator: "+")[0].split(separator: ".").map { Int($0) ?? 0 }
            return p + Array(repeating: 0, count: max(0, 3 - p.count))
        }
        for (x, y) in zip(parts(a), parts(b)) where x != y { return x > y }
        return false
    }

    private static func run(_ exe: String, _ args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: exe)
        p.arguments = args
        try p.run()
        p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw SlateError("\(exe) failed (\(p.terminationStatus)).") }
    }
}
