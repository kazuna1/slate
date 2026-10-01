// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Slate",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "Slate",
            path: "Sources/Slate"
        ),
    ]
)
