import Foundation

private struct HistoryCacheDocument: Codable {
    var version = 1
    var files: [String: HistoryCacheEntry] = [:]
}

private struct HistoryCacheEntry: Codable {
    let length: UInt64
    let modified: TimeInterval
    let days: [String: Int64]
    let totalTokens: Int64
    let firstDay: String?
}

private final class JSONLineReader {
    private let handle: FileHandle
    private var buffer = Data()
    private var reachedEnd = false

    init(url: URL) throws {
        handle = try FileHandle(forReadingFrom: url)
    }

    deinit {
        try? handle.close()
    }

    func next() throws -> Data? {
        while true {
            if let newline = buffer.firstIndex(of: 0x0A) {
                let line = buffer[..<newline]
                buffer.removeSubrange(...newline)
                return Data(line)
            }
            if reachedEnd {
                guard !buffer.isEmpty else { return nil }
                defer { buffer.removeAll(keepingCapacity: false) }
                return buffer
            }
            let chunk = try handle.read(upToCount: 64 * 1024) ?? Data()
            if chunk.isEmpty {
                reachedEnd = true
            } else {
                buffer.append(chunk)
                if buffer.count > 4 * 1024 * 1024 {
                    throw CocoaError(.fileReadCorruptFile)
                }
            }
        }
    }
}

final class TokenHistoryService: @unchecked Sendable {
    private let store: PreferencesStore
    private let calendar = Calendar.autoupdatingCurrent
    private let maxCacheBytes = 8 * 1024 * 1024

    init(store: PreferencesStore = .shared) {
        self.store = store
    }

    func read() -> TokenHistorySnapshot {
        let files = sessionFiles()
        guard !files.isEmpty else {
            return TokenHistorySnapshot(
                available: true,
                status: "ok",
                message: nil,
                sampledAt: Date(),
                since: nil,
                totalTokens: 0,
                todayTokens: 0,
                weekTokens: 0,
                monthTokens: 0,
                sessionFiles: 0,
                reusedFiles: 0,
                days: []
            )
        }

        var cache = readCache()
        var nextFiles: [String: HistoryCacheEntry] = [:]
        var totals: [String: Int64] = [:]
        var reused = 0
        var earliest: String?

        for file in files {
            let key = file.standardizedFileURL.path
            guard let values = try? file.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]) else {
                continue
            }
            let length = UInt64(max(0, values.fileSize ?? 0))
            let modified = values.contentModificationDate?.timeIntervalSince1970 ?? 0
            let entry: HistoryCacheEntry
            if let cached = cache.files[key], cached.length == length, abs(cached.modified - modified) < 0.001 {
                entry = cached
                reused += 1
            } else {
                entry = scan(file, length: length, modified: modified)
            }
            nextFiles[key] = entry
            for (day, count) in entry.days {
                totals[day] = safeAdd(totals[day] ?? 0, count)
            }
            if let first = entry.firstDay, earliest == nil || first < earliest! {
                earliest = first
            }
        }
        cache.files = nextFiles
        writeCache(cache)

        let dayFormatter = Self.dayFormatter
        let ordered = totals.compactMap { key, value -> DailyTokenUsage? in
            guard let date = dayFormatter.date(from: key) else { return nil }
            return DailyTokenUsage(day: date, tokens: value)
        }.sorted { $0.day < $1.day }

        let now = Date()
        let today = calendar.startOfDay(for: now)
        let weekStart = calendar.dateInterval(of: .weekOfYear, for: now)?.start ?? today
        let monthStart = calendar.dateInterval(of: .month, for: now)?.start ?? today
        let total = ordered.reduce(Int64(0)) { safeAdd($0, $1.tokens) }
        let todayTokens = ordered.filter { calendar.isDate($0.day, inSameDayAs: today) }
            .reduce(Int64(0)) { safeAdd($0, $1.tokens) }
        let weekTokens = ordered.filter { $0.day >= weekStart }
            .reduce(Int64(0)) { safeAdd($0, $1.tokens) }
        let monthTokens = ordered.filter { $0.day >= monthStart }
            .reduce(Int64(0)) { safeAdd($0, $1.tokens) }

        return TokenHistorySnapshot(
            available: true,
            status: "ok",
            message: nil,
            sampledAt: Date(),
            since: earliest.flatMap(dayFormatter.date),
            totalTokens: total,
            todayTokens: todayTokens,
            weekTokens: weekTokens,
            monthTokens: monthTokens,
            sessionFiles: files.count,
            reusedFiles: reused,
            days: ordered
        )
    }

    private func sessionFiles() -> [URL] {
        let environment = ProcessInfo.processInfo.environment
        let home: URL
        if let custom = environment["CODEX_HOME"], !custom.isEmpty {
            home = URL(fileURLWithPath: NSString(string: custom).expandingTildeInPath)
        } else {
            home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        }
        let roots = [
            home.appendingPathComponent("sessions", isDirectory: true),
            home.appendingPathComponent("archived_sessions", isDirectory: true)
        ]
        var result: [URL] = []
        let keys: [URLResourceKey] = [.isRegularFileKey]
        for root in roots {
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            ) else { continue }
            for case let url as URL in enumerator where url.pathExtension.lowercased() == "jsonl" {
                if (try? url.resourceValues(forKeys: Set(keys)).isRegularFile) == true {
                    result.append(url)
                }
            }
        }
        return result.sorted { $0.path < $1.path }
    }

    private func scan(_ file: URL, length: UInt64, modified: TimeInterval) -> HistoryCacheEntry {
        var daily: [String: Int64] = [:]
        var total: Int64 = 0
        var previousCumulative: Int64 = 0
        var firstDay: String?
        guard let reader = try? JSONLineReader(url: file) else {
            return HistoryCacheEntry(length: length, modified: modified, days: [:], totalTokens: 0, firstDay: nil)
        }
        while true {
            let next: Data?
            do {
                next = try reader.next()
            } catch {
                break
            }
            guard let line = next else { break }
            guard line.range(of: Data(#""token_count""#.utf8)) != nil else { continue }
            guard
                let root = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                root["type"] as? String == "event_msg",
                let payload = root["payload"] as? [String: Any],
                payload["type"] as? String == "token_count",
                let info = payload["info"] as? [String: Any],
                let timestampText = root["timestamp"] as? String,
                let timestamp = Self.isoDate(timestampText)
            else { continue }

            let totalUsage = info["total_token_usage"] as? [String: Any]
            let lastUsage = info["last_token_usage"] as? [String: Any]
            let cumulative = Self.int64(totalUsage?["total_tokens"])
            let incremental = Self.int64(lastUsage?["total_tokens"])
            let delta: Int64
            if cumulative > 0 {
                delta = cumulative >= previousCumulative ? cumulative - previousCumulative : cumulative
                previousCumulative = cumulative
            } else {
                delta = incremental
                previousCumulative = safeAdd(previousCumulative, delta)
            }
            guard delta > 0 else { continue }
            let localDay = Self.dayFormatter.string(from: timestamp)
            daily[localDay] = safeAdd(daily[localDay] ?? 0, delta)
            total = safeAdd(total, delta)
            if firstDay == nil || localDay < firstDay! { firstDay = localDay }
        }
        return HistoryCacheEntry(
            length: length,
            modified: modified,
            days: daily,
            totalTokens: total,
            firstDay: firstDay
        )
    }

    private func readCache() -> HistoryCacheDocument {
        guard
            let attributes = try? FileManager.default.attributesOfItem(atPath: store.historyCacheURL.path),
            let size = attributes[.size] as? NSNumber,
            size.intValue > 0,
            size.intValue <= maxCacheBytes,
            let data = try? Data(contentsOf: store.historyCacheURL),
            let cache = try? JSONDecoder().decode(HistoryCacheDocument.self, from: data),
            cache.version == 1
        else { return HistoryCacheDocument() }
        return cache
    }

    private func writeCache(_ cache: HistoryCacheDocument) {
        guard let data = try? JSONEncoder().encode(cache), data.count <= maxCacheBytes else { return }
        do {
            try data.write(to: store.historyCacheURL, options: .atomic)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: store.historyCacheURL.path
            )
        } catch {
            // The next refresh can rebuild the cache from source sessions.
        }
    }

    private func safeAdd(_ left: Int64, _ right: Int64) -> Int64 {
        let (sum, overflow) = left.addingReportingOverflow(right)
        return overflow ? Int64.max : sum
    }

    private static func int64(_ value: Any?) -> Int64 {
        if let number = value as? NSNumber { return number.int64Value }
        if let text = value as? String { return Int64(text) ?? 0 }
        return 0
    }

    private static func isoDate(_ value: String) -> Date? {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        if let date = formatter.date(from: value) { return date }
        formatter.formatOptions = [.withInternetDateTime]
        return formatter.date(from: value)
    }

    private static let dayFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .autoupdatingCurrent
        formatter.dateFormat = "yyyy-MM-dd"
        return formatter
    }()
}
