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
    private let maxContextTailBytes: UInt64 = 4 * 1024 * 1024
    private let maxContextCandidateFiles = 24

    init(store: PreferencesStore = .shared) {
        self.store = store
    }

    func fixtureSelfTest() -> [String] {
        let compacted = """
        {"timestamp":"2026-07-21T04:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":191751509},"last_token_usage":{"input_tokens":0,"cached_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0,"total_tokens":45442},"model_context_window":258400}}}
        """
        var failures: [String] = []
        guard let snapshot = parseContext(Data(compacted.utf8)) else {
            return [
                "compacted context fixture should parse",
                "compacted context should derive occupied input",
                "compacted context should avoid false zero percent"
            ]
        }
        if snapshot.inputTokens != 45_442 {
            failures.append("compacted context should derive occupied input")
        }
        let expectedPercent = Double(45_442) * 100 / Double(258_400)
        if abs((snapshot.usedPercent ?? -1) - expectedPercent) > 0.001 {
            failures.append("compacted context should avoid false zero percent")
        }
        if snapshot.inputBreakdownAvailable {
            failures.append("compacted context should mark input breakdown unavailable")
        }
        if Self.activeConversationID(
            from: "2026-07-21T04:00:00Z info thread_stream_view_activity_changed active=true conversationId=01234567-89ab-cdef-0123-456789abcdef rendererWindowAppearance=primary rendererWindowVisible=true"
        ) != "01234567-89ab-cdef-0123-456789abcdef" {
            failures.append("active Codex log line should expose foreground conversation")
        }

        let temporary = FileManager.default.temporaryDirectory
            .appendingPathComponent("CodexQuotaOrb.Context.\(UUID().uuidString)", isDirectory: true)
        do {
            try FileManager.default.createDirectory(at: temporary, withIntermediateDirectories: true)
            defer { try? FileManager.default.removeItem(at: temporary) }
            let active = temporary.appendingPathComponent(
                "rollout-2026-07-20T00-00-00-01234567-89ab-cdef-0123-456789abcdef.jsonl"
            )
            let background = temporary.appendingPathComponent(
                "rollout-2026-07-20T00-00-00-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"
            )
            let activeLines = """
            {"timestamp":"2026-07-21T03:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":300},"last_token_usage":{"input_tokens":90,"cached_input_tokens":70,"output_tokens":10,"reasoning_output_tokens":4,"total_tokens":100},"model_context_window":200}}}
            {"timestamp":"2026-07-21T03:30:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":300},"last_token_usage":{"input_tokens":0,"cached_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0,"total_tokens":80},"model_context_window":200}}}
            """
            let backgroundLines = """
            {"timestamp":"2026-07-21T04:00:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":77191446},"last_token_usage":{"input_tokens":0,"cached_input_tokens":0,"output_tokens":0,"reasoning_output_tokens":0,"total_tokens":62554},"model_context_window":258400}}}
            """
            try Data(activeLines.utf8).write(to: active)
            try Data(backgroundLines.utf8).write(to: background)
            let selected = selectCurrentContext(from: [background, active])
            if selected?.sessionTotalTokens != 300 {
                failures.append("background compaction should not replace active context")
            }
            if selected?.sessionID != "01234567-89ab-cdef-0123-456789abcdef" {
                failures.append("selected context should expose source session")
            }
            if selected?.sampledAt != Self.isoDate("2026-07-21T03:30:00Z") {
                failures.append("selected context should keep source sample time")
            }
        } catch {
            failures.append("context selection fixture should run")
        }
        return failures
    }

    func read() -> TokenHistorySnapshot {
        let files = sessionFiles()
        let context = readCurrentContext()
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
                days: [],
                context: context
            )
        }

        var cache = readCache()
        var nextFiles: [String: HistoryCacheEntry] = [:]
        var totals: [String: Int64] = [:]
        var conversations: [ConversationTokenUsage] = []
        var projects: [String: ProjectTokenUsage] = [:]
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
            let conversation = readConversation(file, tokens: entry.totalTokens)
            conversations.append(conversation)
            let projectKey = conversation.projectPath ?? "(unknown)"
            var project = projects[projectKey] ?? ProjectTokenUsage(
                projectPath: conversation.projectPath,
                projectName: conversation.projectName
            )
            project.tokens = safeAdd(project.tokens, conversation.tokens)
            project.conversations += 1
            projects[projectKey] = project
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
            days: ordered,
            context: context,
            projects: projects.values.sorted {
                if $0.tokens != $1.tokens { return $0.tokens > $1.tokens }
                return $0.projectName.localizedCaseInsensitiveCompare($1.projectName) == .orderedAscending
            },
            conversations: conversations.sorted {
                if $0.tokens != $1.tokens { return $0.tokens > $1.tokens }
                return $0.updatedAt > $1.updatedAt
            }
        )
    }

    private func readCurrentContext() -> ContextCapacitySnapshot {
        let allFiles = activeSessionFiles()
        let preferredSessionID = readActiveConversationID()
        var candidates = Array(allFiles.prefix(maxContextCandidateFiles))
        if let preferredSessionID,
           !candidates.contains(where: { sessionID(from: $0)?.caseInsensitiveCompare(preferredSessionID) == .orderedSame }),
           let preferredFile = allFiles.first(where: { sessionID(from: $0)?.caseInsensitiveCompare(preferredSessionID) == .orderedSame })
        {
            candidates.append(preferredFile)
        }
        if let selected = selectCurrentContext(
            from: candidates,
            preferredSessionID: preferredSessionID
        ) {
            return selected
        }
        return ContextCapacitySnapshot(
            available: false,
            status: candidates.isEmpty ? "empty" : "unavailable"
        )
    }

    private func selectCurrentContext(
        from files: [URL],
        preferredSessionID: String? = nil
    ) -> ContextCapacitySnapshot? {
        if let preferredSessionID {
            for file in files where sessionID(from: file)?.caseInsensitiveCompare(preferredSessionID) == .orderedSame {
                if var preferred = readContextCandidate(from: file) {
                    preferred.selectionSource = "codex-log"
                    return preferred
                }
            }
        }
        var best: ContextCapacitySnapshot?
        for file in files {
            guard let candidate = readContextCandidate(from: file) else { continue }
            guard let current = best else {
                best = candidate
                continue
            }
            let candidateActivity = candidate.activityAt ?? .distantPast
            let currentActivity = current.activityAt ?? .distantPast
            let candidateSample = candidate.sampledAt ?? .distantPast
            let currentSample = current.sampledAt ?? .distantPast
            if candidateActivity > currentActivity
                || (candidateActivity == currentActivity && candidateSample > currentSample)
            {
                best = candidate
            }
        }
        best?.selectionSource = "activity"
        return best
    }

    private func readContextCandidate(from file: URL) -> ContextCapacitySnapshot? {
        guard let handle = try? FileHandle(forReadingFrom: file) else { return nil }
        defer { try? handle.close() }
        guard let length = try? handle.seekToEnd(), length > 0 else { return nil }
        let start = length > maxContextTailBytes ? length - maxContextTailBytes : 0
        do {
            try handle.seek(toOffset: start)
            guard let data = try handle.readToEnd(), !data.isEmpty else { return nil }
            var latest: ContextCapacitySnapshot?
            var activityAt: Date?
            for rawLine in data.split(separator: 0x0A).reversed() {
                let line = Data(rawLine)
                guard
                    line.range(of: Data(#""token_count""#.utf8)) != nil,
                    line.range(of: Data(#""model_context_window""#.utf8)) != nil,
                    let parsed = parseContext(line)
                else { continue }
                if latest == nil { latest = parsed }
                if parsed.inputBreakdownAvailable, activityAt == nil {
                    activityAt = parsed.sampledAt
                }
                if latest != nil, activityAt != nil { break }
            }
            if var latest {
                latest.activityAt = activityAt
                latest.sessionID = sessionID(from: file)
                return latest
            }
        } catch {
            return nil
        }
        return nil
    }

    private func parseContext(_ line: Data) -> ContextCapacitySnapshot? {
        guard
            let root = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
            root["type"] as? String == "event_msg",
            let payload = root["payload"] as? [String: Any],
            payload["type"] as? String == "token_count",
            let info = payload["info"] as? [String: Any],
            let last = info["last_token_usage"] as? [String: Any]
        else { return nil }

        let capacity = Self.int64(info["model_context_window"])
        var input = Self.int64(last["input_tokens"])
        let rawCached = max(0, Self.int64(last["cached_input_tokens"]))
        let lastTotal = max(0, Self.int64(last["total_tokens"]))
        let output = max(0, Self.int64(last["output_tokens"]))
        let derivedFromCompaction = input == 0 && rawCached == 0 && lastTotal > output
        // A compaction-boundary token_count can zero its input components
        // while last total still carries the compacted context size.
        // total_tokens is input + output; reasoning is an output subset.
        if derivedFromCompaction {
            input = lastTotal - output
        }
        guard capacity > 0, input >= 0 else { return nil }
        let cached = derivedFromCompaction ? 0 : min(input, rawCached)
        let total = info["total_token_usage"] as? [String: Any]
        return ContextCapacitySnapshot(
            available: true,
            status: "ok",
            sampledAt: (root["timestamp"] as? String).flatMap(Self.isoDate),
            capacityTokens: capacity,
            inputTokens: input,
            cachedInputTokens: cached,
            inputBreakdownAvailable: !derivedFromCompaction,
            outputTokens: output,
            reasoningOutputTokens: max(0, Self.int64(last["reasoning_output_tokens"])),
            sessionTotalTokens: max(0, Self.int64(total?["total_tokens"]))
        )
    }

    private func sessionID(from file: URL) -> String? {
        let name = file.deletingPathExtension().lastPathComponent
        guard name.count >= 36 else { return nil }
        let candidate = String(name.suffix(36))
        return UUID(uuidString: candidate) == nil ? nil : candidate
    }

    private func readActiveConversationID() -> String? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        let roots = [
            home.appendingPathComponent("Library/Logs/Codex", isDirectory: true),
            home.appendingPathComponent("Library/Logs/OpenAI/Codex", isDirectory: true),
            home.appendingPathComponent("Library/Logs/com.openai.codex", isDirectory: true),
            home.appendingPathComponent("Library/Application Support/Codex/Logs", isDirectory: true),
            home.appendingPathComponent("Library/Logs", isDirectory: true)
        ]
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .contentModificationDateKey]
        var logs: [(URL, Date)] = []
        var seen = Set<String>()
        for root in roots {
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: Array(keys),
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            ) else { continue }
            for case let url as URL in enumerator
                where url.pathExtension.lowercased() == "log"
                    && url.lastPathComponent.lowercased().hasPrefix("codex-desktop-")
            {
                guard seen.insert(url.standardizedFileURL.path).inserted,
                      let values = try? url.resourceValues(forKeys: keys),
                      values.isRegularFile == true
                else { continue }
                logs.append((url, values.contentModificationDate ?? .distantPast))
            }
        }
        logs.sort {
            if $0.1 != $1.1 { return $0.1 > $1.1 }
            return $0.0.path > $1.0.path
        }
        for (url, _) in logs.prefix(8) {
            guard let handle = try? FileHandle(forReadingFrom: url) else { continue }
            defer { try? handle.close() }
            guard let length = try? handle.seekToEnd(), length > 0 else { continue }
            let start = length > 2 * 1024 * 1024 ? length - 2 * 1024 * 1024 : 0
            do {
                try handle.seek(toOffset: start)
                guard let data = try handle.readToEnd(), !data.isEmpty else { continue }
                for rawLine in data.split(separator: 0x0A).reversed() {
                    guard let line = String(data: rawLine, encoding: .utf8),
                          let conversationID = Self.activeConversationID(from: line)
                    else { continue }
                    return conversationID
                }
            } catch {
                continue
            }
        }
        return nil
    }

    private static func activeConversationID(from line: String) -> String? {
        guard line.contains("thread_stream_view_activity_changed active=true"),
              line.contains("rendererWindowAppearance=primary"),
              line.contains("rendererWindowVisible=true"),
              let marker = line.range(of: "conversationId=")
        else { return nil }
        let tail = line[marker.upperBound...]
        let candidate = String(tail.prefix { !$0.isWhitespace })
        return UUID(uuidString: candidate) == nil ? nil : candidate
    }

    private func activeSessionFiles() -> [URL] {
        let root = codexHome().appendingPathComponent("sessions", isDirectory: true)
        let keys: Set<URLResourceKey> = [.isRegularFileKey, .contentModificationDateKey]
        guard let enumerator = FileManager.default.enumerator(
            at: root,
            includingPropertiesForKeys: Array(keys),
            options: [.skipsHiddenFiles, .skipsPackageDescendants]
        ) else { return [] }
        var files: [(URL, Date)] = []
        for case let url as URL in enumerator where url.pathExtension.lowercased() == "jsonl" {
            guard let values = try? url.resourceValues(forKeys: keys), values.isRegularFile == true else { continue }
            files.append((url, values.contentModificationDate ?? .distantPast))
        }
        return files.sorted {
            if $0.1 != $1.1 { return $0.1 > $1.1 }
            return $0.0.path > $1.0.path
        }.map { $0.0 }
    }

    private func sessionFiles() -> [URL] {
        let home = codexHome()
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

    private func codexHome() -> URL {
        let environment = ProcessInfo.processInfo.environment
        if let custom = environment["CODEX_HOME"], !custom.isEmpty {
            return URL(fileURLWithPath: NSString(string: custom).expandingTildeInPath)
        }
        return FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
    }

    private func readConversation(_ file: URL, tokens: Int64) -> ConversationTokenUsage {
        let values = try? file.resourceValues(forKeys: [.creationDateKey, .contentModificationDateKey])
        var result = ConversationTokenUsage(
            sessionID: file.deletingPathExtension().lastPathComponent,
            startedAt: values?.creationDate ?? .distantPast,
            updatedAt: values?.contentModificationDate ?? .distantPast,
            tokens: max(0, tokens)
        )
        guard let reader = try? JSONLineReader(url: file) else { return result }
        for _ in 0 ..< 24 {
            let next: Data?
            do {
                next = try reader.next()
            } catch {
                break
            }
            guard let line = next else { break }
            guard line.range(of: Data(#""session_meta""#.utf8)) != nil else { continue }
            guard
                let root = try? JSONSerialization.jsonObject(with: line) as? [String: Any],
                root["type"] as? String == "session_meta",
                let payload = root["payload"] as? [String: Any]
            else { continue }
            if let id = payload["id"] as? String, !id.isEmpty {
                result.sessionID = id
            }
            if let cwd = payload["cwd"] as? String, !cwd.isEmpty {
                result.projectPath = cwd
                let name = URL(fileURLWithPath: cwd).standardizedFileURL.lastPathComponent
                result.projectName = name.isEmpty ? cwd : name
            }
            if let timestamp = root["timestamp"] as? String, let started = Self.isoDate(timestamp) {
                result.startedAt = started
            }
            break
        }
        return result
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
