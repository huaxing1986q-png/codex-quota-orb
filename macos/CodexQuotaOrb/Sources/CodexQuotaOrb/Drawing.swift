import AppKit

enum Typography {
    static func system(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        NSFont.systemFont(ofSize: size, weight: weight)
    }

    static func mono(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        NSFont.monospacedDigitSystemFont(ofSize: size, weight: weight)
    }
}

enum Copy {
    static func weekly(_ language: AppLanguage) -> String {
        language == .chinese ? "每周" : "WEEKLY"
    }

    static func reset(_ language: AppLanguage, date: Date?) -> String {
        guard let date else { return language == .chinese ? "重置时间 —" : "RESET —" }
        let formatter = DateFormatter()
        formatter.locale = language == .chinese ? Locale(identifier: "zh_CN") : Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = language == .chinese ? "M-d HH:mm" : "MMM d, HH:mm"
        return (language == .chinese ? "重置 " : "RESET ") + formatter.string(from: date)
    }

    static func health(_ language: AppLanguage, value: QuotaHealth) -> String {
        switch (language, value) {
        case (.chinese, .healthy): return "健康"
        case (.chinese, .caution): return "谨慎"
        case (.chinese, .critical): return "紧急"
        case (.chinese, .unavailable): return "不可用"
        case (.english, .healthy): return "HEALTHY"
        case (.english, .caution): return "CAUTION"
        case (.english, .critical): return "CRITICAL"
        case (.english, .unavailable): return "OFFLINE"
        }
    }

    static func tokenDetails(_ language: AppLanguage) -> String {
        language == .chinese ? "Codex Token 使用详情" : "Codex Token Usage"
    }

    static func localDisclosure(_ language: AppLanguage) -> String {
        language == .chinese
            ? "上方与下方为本机 Token 历史；中间为全部对话累计上下文。"
            : "Top and bottom show local Token history; the middle shows cumulative context across all conversations."
    }

    static func quitPlugin(_ language: AppLanguage) -> String {
        language == .chinese ? "退出插件" : "Quit Plugin"
    }
}

extension NSColor {
    func withAlpha(_ alpha: CGFloat) -> NSColor {
        withAlphaComponent(alpha)
    }
}

func drawText(
    _ text: String,
    in rect: NSRect,
    font: NSFont,
    color: NSColor,
    alignment: NSTextAlignment = .left,
    lineBreak: NSLineBreakMode = .byTruncatingTail
) {
    let paragraph = NSMutableParagraphStyle()
    paragraph.alignment = alignment
    paragraph.lineBreakMode = lineBreak
    (text as NSString).draw(
        in: rect,
        withAttributes: [
            .font: font,
            .foregroundColor: color,
            .paragraphStyle: paragraph
        ]
    )
}

func roundedPath(_ rect: NSRect, radius: CGFloat) -> NSBezierPath {
    NSBezierPath(roundedRect: rect, xRadius: radius, yRadius: radius)
}

func formatTokens(_ tokens: Int64, language: AppLanguage) -> String {
    let value = Double(tokens)
    if language == .chinese {
        if value >= 100_000_000 {
            return String(format: "%.2f 亿", value / 100_000_000)
        }
        if value >= 10_000 {
            return String(format: "%.1f 万", value / 10_000)
        }
    } else {
        if value >= 1_000_000_000 { return String(format: "%.2fB", value / 1_000_000_000) }
        if value >= 1_000_000 { return String(format: "%.2fM", value / 1_000_000) }
        if value >= 1_000 { return String(format: "%.1fK", value / 1_000) }
    }
    return NumberFormatter.localizedString(from: NSNumber(value: tokens), number: .decimal)
}
