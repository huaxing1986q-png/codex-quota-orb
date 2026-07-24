import Foundation

final class PreferencesStore {
    static let shared = PreferencesStore()

    let supportDirectory: URL
    let preferencesURL: URL
    let historyCacheURL: URL

    private let encoder: JSONEncoder
    private let decoder: JSONDecoder

    private init() {
        let base = FileManager.default.urls(
            for: .applicationSupportDirectory,
            in: .userDomainMask
        ).first!
        supportDirectory = base.appendingPathComponent("CodexQuotaOrb", isDirectory: true)
        preferencesURL = supportDirectory.appendingPathComponent("preferences.json")
        historyCacheURL = supportDirectory.appendingPathComponent("token-history-cache.json")
        encoder = JSONEncoder()
        decoder = JSONDecoder()
        encoder.outputFormatting = [.sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        decoder.dateDecodingStrategy = .iso8601
        try? FileManager.default.createDirectory(
            at: supportDirectory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: 0o700]
        )
    }

    func load() -> WidgetPreferences {
        guard
            let data = try? Data(contentsOf: preferencesURL),
            data.count <= 64 * 1024,
            let value = try? decoder.decode(WidgetPreferences.self, from: data)
        else {
            return WidgetPreferences()
        }
        return value
    }

    func save(_ preferences: WidgetPreferences) {
        guard let data = try? encoder.encode(preferences) else { return }
        do {
            try data.write(to: preferencesURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: preferencesURL.path
            )
        } catch {
            // Preferences are a convenience. The widget remains usable if this
            // sandboxed write is denied.
        }
    }
}
