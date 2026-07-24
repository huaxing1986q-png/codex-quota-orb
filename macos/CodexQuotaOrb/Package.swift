// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "CodexQuotaOrbMac",
    platforms: [
        .macOS(.v13)
    ],
    products: [
        .executable(name: "CodexQuotaOrb", targets: ["CodexQuotaOrb"])
    ],
    targets: [
        .executableTarget(
            name: "CodexQuotaOrb",
            path: "Sources/CodexQuotaOrb",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("QuartzCore")
            ]
        )
    ]
)
