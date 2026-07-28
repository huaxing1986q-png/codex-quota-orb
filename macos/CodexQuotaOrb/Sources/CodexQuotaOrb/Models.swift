import AppKit
import Foundation

enum AppLanguage: String, Codable {
    case chinese = "zh-CN"
    case english = "en"

    static var systemDefault: AppLanguage {
        Locale.preferredLanguages.first?.lowercased().hasPrefix("zh") == true ? .chinese : .english
    }
}

enum QuotaHealth {
    case healthy
    case caution
    case critical
    case unavailable
}

struct QuotaSnapshot {
    var available = false
    var plan = "--"
    var weeklyRemaining: Double?
    var weeklyReset: Date?
    var sampledAt = Date()
    var status = "loading"
    var message: String?

    var health: QuotaHealth {
        guard available, let remaining = weeklyRemaining else { return .unavailable }
        if remaining >= 50 { return .healthy }
        if remaining >= 10 { return .caution }
        return .critical
    }

    static func failure(_ status: String, _ message: String) -> QuotaSnapshot {
        QuotaSnapshot(
            available: false,
            plan: "--",
            weeklyRemaining: nil,
            weeklyReset: nil,
            sampledAt: Date(),
            status: status,
            message: message
        )
    }
}

struct DailyTokenUsage: Codable, Hashable {
    let day: Date
    let tokens: Int64
}

struct ContextCapacitySnapshot {
    var available = false
    var status = "unavailable"
    var sampledAt: Date?
    var capacityTokens: Int64 = 0
    var inputTokens: Int64 = 0
    var cachedInputTokens: Int64 = 0
    var outputTokens: Int64 = 0
    var reasoningOutputTokens: Int64 = 0
    var sessionTotalTokens: Int64 = 0

    var freshInputTokens: Int64 {
        max(0, inputTokens - cachedInputTokens)
    }

    var remainingTokens: Int64 {
        max(0, capacityTokens - inputTokens)
    }

    var usedPercent: Double? {
        guard available, capacityTokens > 0 else { return nil }
        return min(100, max(0, Double(inputTokens) * 100 / Double(capacityTokens)))
    }
}

struct ConversationTokenUsage {
    var sessionID = "unknown"
    var projectPath: String?
    var projectName = "Unknown project"
    var startedAt = Date.distantPast
    var updatedAt = Date.distantPast
    var tokens: Int64 = 0
}

struct ProjectTokenUsage {
    var projectPath: String?
    var projectName = "Unknown project"
    var tokens: Int64 = 0
    var conversations = 0
}

struct TokenHistorySnapshot {
    var available = false
    var status = "loading"
    var message: String?
    var sampledAt = Date()
    var since: Date?
    var totalTokens: Int64 = 0
    var todayTokens: Int64 = 0
    var weekTokens: Int64 = 0
    var monthTokens: Int64 = 0
    var sessionFiles = 0
    var reusedFiles = 0
    var days: [DailyTokenUsage] = []
    var context = ContextCapacitySnapshot()
    var projects: [ProjectTokenUsage] = []
    var conversations: [ConversationTokenUsage] = []
}

enum TokenDetailView: Int {
    case daily
    case weekly
    case cumulative
}

struct WidgetPreferences: Codable {
    var language: AppLanguage = .systemDefault
    var alwaysOnTop = false
    var hasCustomAnchor = false
    var anchorX: Double = 0
    var anchorY: Double = 0
}

enum Palette {
    static let canvas = NSColor(calibratedRed: 234 / 255, green: 242 / 255, blue: 248 / 255, alpha: 1)
    static let surface = NSColor(calibratedRed: 246 / 255, green: 249 / 255, blue: 251 / 255, alpha: 1)
    static let text = NSColor(calibratedRed: 23 / 255, green: 27 / 255, blue: 34 / 255, alpha: 1)
    static let secondary = NSColor(calibratedRed: 93 / 255, green: 102 / 255, blue: 114 / 255, alpha: 1)
    static let stroke = NSColor(calibratedRed: 202 / 255, green: 215 / 255, blue: 226 / 255, alpha: 1)
    static let accent = NSColor(calibratedRed: 57 / 255, green: 122 / 255, blue: 224 / 255, alpha: 1)
    static let accentSoft = NSColor(calibratedRed: 145 / 255, green: 186 / 255, blue: 240 / 255, alpha: 1)
    static let healthy = NSColor(calibratedRed: 51 / 255, green: 200 / 255, blue: 120 / 255, alpha: 1)
    static let caution = NSColor(calibratedRed: 214 / 255, green: 155 / 255, blue: 45 / 255, alpha: 1)
    static let critical = NSColor(calibratedRed: 233 / 255, green: 93 / 255, blue: 79 / 255, alpha: 1)

    static func status(_ health: QuotaHealth) -> NSColor {
        switch health {
        case .healthy: return healthy
        case .caution: return caution
        case .critical: return critical
        case .unavailable: return secondary
        }
    }
}

extension NSRect {
    func clamped(to bounds: NSRect) -> NSRect {
        var result = self
        result.origin.x = max(bounds.minX, min(bounds.maxX - width, result.origin.x))
        result.origin.y = max(bounds.minY, min(bounds.maxY - height, result.origin.y))
        return result
    }
}
