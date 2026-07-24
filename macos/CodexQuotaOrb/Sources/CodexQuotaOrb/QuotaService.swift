import Foundation

private final class NoRedirectDelegate: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

final class QuotaService: @unchecked Sendable {
    private let usageURL = URL(string: "https://chatgpt.com/backend-api/wham/usage")!
    private let maxAuthBytes = 256 * 1024
    private let maxResponseBytes = 1024 * 1024

    func fetch() async -> QuotaSnapshot {
        let auth: (token: String, accountID: String?)
        do {
            auth = try loadAuth()
        } catch let error as QuotaError {
            return .failure(error.status, error.message)
        } catch {
            return .failure("signed_out", "Please sign in to Codex Desktop first.")
        }

        var request = URLRequest(url: usageURL)
        request.httpMethod = "GET"
        request.timeoutInterval = 8
        request.cachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("CodexQuotaOrb/0.3 macOS", forHTTPHeaderField: "User-Agent")
        request.setValue("Bearer \(auth.token)", forHTTPHeaderField: "Authorization")
        request.setValue("Codex Desktop", forHTTPHeaderField: "originator")
        request.setValue("CODEX", forHTTPHeaderField: "OAI-Product-Sku")
        if let accountID = auth.accountID, !accountID.isEmpty {
            request.setValue(accountID, forHTTPHeaderField: "ChatGPT-Account-Id")
        }

        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 8
        configuration.timeoutIntervalForResource = 8
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        let session = URLSession(
            configuration: configuration,
            delegate: NoRedirectDelegate(),
            delegateQueue: nil
        )
        defer { session.invalidateAndCancel() }

        do {
            let (data, response) = try await session.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return .failure("unavailable", "Quota service is temporarily unavailable.")
            }
            guard (200 ... 299).contains(http.statusCode) else {
                switch http.statusCode {
                case 401, 403:
                    return .failure("signed_out", "Codex login expired. Please sign in again.")
                case 429:
                    return .failure("unavailable", "Quota service is rate limited. Retrying automatically.")
                default:
                    return .failure("unavailable", "Quota service is temporarily unavailable.")
                }
            }
            guard !data.isEmpty, data.count <= maxResponseBytes else {
                return .failure("unavailable", "Quota response is unavailable.")
            }
            return parseUsage(data)
        } catch {
            return .failure("unavailable", "Network unavailable. Retrying automatically.")
        }
    }

    func fixtureSelfTest() -> [String] {
        let fixture = """
        {"plan_type":"prolite","rate_limit":{"primary_window":{"used_percent":49,"reset_at":1784956095,"limit_window_seconds":604800},"secondary_window":null}}
        """
        let snapshot = parseUsage(Data(fixture.utf8))
        var failures: [String] = []
        if !snapshot.available { failures.append("weekly-only fixture should be available") }
        if abs((snapshot.weeklyRemaining ?? -1) - 51) > 0.001 {
            failures.append("weekly remaining should be 51 percent")
        }
        if snapshot.plan != "PROLITE" { failures.append("plan should normalize to uppercase") }
        if snapshot.weeklyReset == nil { failures.append("weekly reset should parse") }
        return failures
    }

    private func loadAuth() throws -> (token: String, accountID: String?) {
        let environment = ProcessInfo.processInfo.environment
        let codexHome: URL
        if let custom = environment["CODEX_HOME"], !custom.trimmingCharacters(in: .whitespaces).isEmpty {
            codexHome = URL(fileURLWithPath: NSString(string: custom).expandingTildeInPath)
        } else {
            codexHome = FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".codex", isDirectory: true)
        }
        let url = codexHome.appendingPathComponent("auth.json")
        let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
        guard
            let size = attributes?[.size] as? NSNumber,
            size.intValue > 0,
            size.intValue <= maxAuthBytes,
            let data = try? Data(contentsOf: url, options: .mappedIfSafe),
            let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            throw QuotaError(status: "signed_out", message: "Please sign in to Codex Desktop first.")
        }
        let tokens = dictionary(root["tokens"]) ?? root
        guard let accessToken = string(tokens, keys: ["access_token", "accessToken"]), !accessToken.isEmpty else {
            throw QuotaError(status: "signed_out", message: "Codex login expired. Please sign in again.")
        }
        let accountID = string(tokens, keys: ["account_id", "accountId"]) ?? accountIDFromJWT(accessToken)
        return (accessToken, accountID)
    }

    private func accountIDFromJWT(_ token: String) -> String? {
        let parts = token.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        var payload = String(parts[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        while payload.count % 4 != 0 { payload.append("=") }
        guard
            let data = Data(base64Encoded: payload),
            let values = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else { return nil }
        return string(values, keys: [
            "https://api.openai.com/auth.chatgpt_account_id",
            "chatgpt_account_id"
        ])
    }

    private func parseUsage(_ data: Data) -> QuotaSnapshot {
        guard
            let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        else {
            return .failure("unavailable", "Quota response format has changed.")
        }
        let rateLimit = dictionary(root["rate_limit"]) ?? dictionary(root["rateLimit"]) ?? root
        guard let weeklyValues = findWeeklyWindow(rateLimit), let parsed = parseWindow(weeklyValues) else {
            return .failure("unavailable", "Quota response does not contain a weekly quota window.")
        }
        return QuotaSnapshot(
            available: true,
            plan: (string(root, keys: ["plan_type", "planType"]) ?? "--").uppercased(),
            weeklyRemaining: parsed.remaining,
            weeklyReset: parsed.reset,
            sampledAt: Date(),
            status: "ok",
            message: nil
        )
    }

    private func findWeeklyWindow(_ values: [String: Any]) -> [String: Any]? {
        let directCandidates = values.values.compactMap { dictionary($0) }
        if let exact = directCandidates.first(where: { candidate in
            guard let parsed = parseWindow(candidate) else { return false }
            return abs(parsed.windowSeconds - 604_800) <= 60
        }) {
            return exact
        }

        let collectionKeys = ["windows", "limit_windows", "limitWindows", "limits", "buckets"]
        for key in collectionKeys {
            guard let items = values[key] as? [Any] else { continue }
            for item in items {
                guard let candidate = dictionary(item), let parsed = parseWindow(candidate) else { continue }
                if abs(parsed.windowSeconds - 604_800) <= 60 { return candidate }
            }
        }

        let semanticKeys = [
            "secondary_window", "secondaryWindow", "weekly_window", "weeklyWindow",
            "week_window", "weekWindow", "weekly", "secondary"
        ]
        for key in semanticKeys {
            guard let candidate = dictionary(values[key]), let parsed = parseWindow(candidate) else { continue }
            if parsed.windowSeconds <= 0 { return candidate }
        }
        for key in collectionKeys {
            guard let items = values[key] as? [Any] else { continue }
            for item in items {
                guard let candidate = dictionary(item), let parsed = parseWindow(candidate) else { continue }
                let name = string(candidate, keys: ["name", "type", "id", "window", "label"])?.lowercased() ?? ""
                if parsed.windowSeconds <= 0 && (name.contains("week") || name.contains("secondary")) {
                    return candidate
                }
            }
        }
        return nil
    }

    private func parseWindow(_ values: [String: Any]) -> (remaining: Double, reset: Date?, windowSeconds: Double)? {
        let remainingKeys = [
            "remaining_percent", "remainingPercent", "remaining_pct", "remainingPct",
            "remaining_ratio", "remainingRatio", "remaining"
        ]
        let usedKeys = [
            "used_percent", "usedPercent", "used_pct", "usedPct",
            "used_ratio", "usedRatio", "utilization", "used"
        ]
        var remaining: Double?
        for key in remainingKeys {
            if let value = number(values[key]) {
                remaining = shouldScaleAsRatio(key: key, value: value) ? value * 100 : value
                break
            }
        }
        if remaining == nil {
            for key in usedKeys {
                if let value = number(values[key]) {
                    let used = shouldScaleAsRatio(key: key, value: value) ? value * 100 : value
                    remaining = 100 - used
                    break
                }
            }
        }
        guard let remaining else { return nil }
        let windowSeconds = numberValue(values, keys: [
            "limit_window_seconds", "limitWindowSeconds", "window_seconds", "windowSeconds",
            "duration_seconds", "durationSeconds", "period_seconds", "periodSeconds"
        ]) ?? 0
        let reset = dateValue(values, keys: [
            "reset_at", "resetAt", "resets_at", "resetsAt", "reset_time", "resetTime"
        ])
        return (min(100, max(0, remaining)), reset, windowSeconds)
    }

    private func shouldScaleAsRatio(key: String, value: Double) -> Bool {
        let lower = key.lowercased()
        return lower.contains("ratio")
            || lower == "utilization"
            || (!lower.contains("percent") && !lower.contains("pct") && value <= 1)
    }

    private func dateValue(_ values: [String: Any], keys: [String]) -> Date? {
        for key in keys {
            guard let value = values[key] else { continue }
            if let seconds = number(value) {
                let epoch = seconds > 10_000_000_000 ? seconds / 1000 : seconds
                return Date(timeIntervalSince1970: epoch)
            }
            if let text = value as? String {
                let formatter = ISO8601DateFormatter()
                formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
                if let date = formatter.date(from: text) { return date }
                formatter.formatOptions = [.withInternetDateTime]
                if let date = formatter.date(from: text) { return date }
            }
        }
        return nil
    }

    private func dictionary(_ value: Any?) -> [String: Any]? {
        value as? [String: Any]
    }

    private func string(_ values: [String: Any], keys: [String]) -> String? {
        for key in keys {
            if let text = values[key] as? String { return text }
        }
        return nil
    }

    private func numberValue(_ values: [String: Any], keys: [String]) -> Double? {
        for key in keys {
            if let value = number(values[key]) { return value }
        }
        return nil
    }

    private func number(_ value: Any?) -> Double? {
        if let value = value as? NSNumber { return value.doubleValue }
        if let value = value as? String { return Double(value) }
        return nil
    }
}

private struct QuotaError: Error {
    let status: String
    let message: String
}
