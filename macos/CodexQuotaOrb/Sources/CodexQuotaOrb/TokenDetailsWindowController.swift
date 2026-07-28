import AppKit

final class TokenDetailsWindowController: NSWindowController, NSWindowDelegate {
    private let detailsView: TokenDetailsView
    private let contextBreakdown: ContextBreakdownWindowController
    var onRefresh: (() -> Void)? {
        didSet { detailsView.onRefresh = onRefresh }
    }
    var onVisibilityChanged: ((Bool) -> Void)?

    init(snapshot: TokenHistorySnapshot, quota: QuotaSnapshot, language: AppLanguage) {
        detailsView = TokenDetailsView(frame: NSRect(x: 0, y: 0, width: 1120, height: 780))
        contextBreakdown = ContextBreakdownWindowController(snapshot: snapshot, language: language)
        detailsView.snapshot = snapshot
        detailsView.quota = quota
        detailsView.language = language
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1120, height: 780),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.contentView = detailsView
        window.minSize = NSSize(width: 920, height: 690)
        window.title = Copy.tokenDetails(language)
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.backgroundColor = Palette.canvas
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenPrimary]
        super.init(window: window)
        window.delegate = self
        detailsView.onClose = { [weak window] in window?.performClose(nil) }
        detailsView.onContextDetails = { [weak self] in
            guard let self else { return }
            self.contextBreakdown.update(snapshot: self.detailsView.snapshot, language: self.detailsView.language)
            self.contextBreakdown.present(relativeTo: self.window)
        }
        contextBreakdown.onReturn = { [weak self] in
            self?.window?.makeKeyAndOrderFront(nil)
            self?.window?.makeFirstResponder(self?.detailsView)
        }
    }

    required init?(coder: NSCoder) {
        nil
    }

    var isDetailsVisible: Bool {
        window?.isVisible == true
    }

    func update(snapshot: TokenHistorySnapshot, quota: QuotaSnapshot, language: AppLanguage) {
        detailsView.snapshot = snapshot
        detailsView.quota = quota
        detailsView.language = language
        contextBreakdown.update(snapshot: snapshot, language: language)
        window?.title = Copy.tokenDetails(language)
        detailsView.needsDisplay = true
    }

    func present() {
        guard let window else { return }
        if !window.isVisible { window.center() }
        if window.isMiniaturized { window.deminiaturize(nil) }
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(detailsView)
        onVisibilityChanged?(true)
    }

    func windowWillClose(_ notification: Notification) {
        contextBreakdown.closeForParent()
        onVisibilityChanged?(false)
    }

    func windowDidMiniaturize(_ notification: Notification) {
        contextBreakdown.closeForParent()
        onVisibilityChanged?(false)
    }

    func windowDidDeminiaturize(_ notification: Notification) {
        onVisibilityChanged?(true)
    }
}

private final class TokenDetailsView: NSView {
    var snapshot = TokenHistorySnapshot()
    var quota = QuotaSnapshot()
    var language: AppLanguage = .systemDefault
    var selected: TokenDetailView = .daily
    var onRefresh: (() -> Void)?
    var onClose: (() -> Void)?
    var onContextDetails: (() -> Void)?

    private var tabRects: [(TokenDetailView, NSRect)] = []
    private var hoveredTab: TokenDetailView?
    private var trackingReference: NSTrackingArea?
    private var contextRect = NSRect.zero
    private var hoveringContext = false

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }

    override func updateTrackingAreas() {
        if let trackingReference { removeTrackingArea(trackingReference) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeInKeyWindow, .mouseMoved, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingReference = area
        super.updateTrackingAreas()
    }

    override func mouseMoved(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let nextContext = contextRect.contains(point)
        if nextContext != hoveringContext {
            hoveringContext = nextContext
            needsDisplay = true
        }
        (nextContext ? NSCursor.pointingHand : NSCursor.arrow).set()
        let next = tabRects.first { $0.1.contains(point) }?.0
        if next != hoveredTab {
            hoveredTab = next
            needsDisplay = true
        }
    }

    override func mouseExited(with event: NSEvent) {
        hoveredTab = nil
        hoveringContext = false
        NSCursor.arrow.set()
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if contextRect.contains(point) {
            onContextDetails?()
            return
        }
        if let tab = tabRects.first(where: { $0.1.contains(point) })?.0 {
            selected = tab
            needsDisplay = true
        }
    }

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 53:
            onClose?()
        case 123:
            selected = TokenDetailView(rawValue: max(0, selected.rawValue - 1)) ?? .daily
            needsDisplay = true
        case 124:
            selected = TokenDetailView(rawValue: min(2, selected.rawValue + 1)) ?? .cumulative
            needsDisplay = true
        case 96:
            onRefresh?()
        default:
            let key = event.charactersIgnoringModifiers?.lowercased()
            if key == "r" && event.modifierFlags.contains(.command) {
                onRefresh?()
            } else {
                super.keyDown(with: event)
            }
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .high
        drawBackground()
        drawHeader()
        drawMetrics()
        drawContextCapacity()
        drawActivity()
    }

    private func drawBackground() {
        NSGradient(colors: [
            NSColor(calibratedRed: 232 / 255, green: 244 / 255, blue: 251 / 255, alpha: 1),
            NSColor(calibratedWhite: 0.985, alpha: 1),
            NSColor(calibratedRed: 243 / 255, green: 246 / 255, blue: 247 / 255, alpha: 1)
        ])?.draw(in: bounds, angle: -12)

        let glow = NSBezierPath(ovalIn: NSRect(x: -180, y: -190, width: 600, height: 500))
        Palette.accentSoft.withAlpha(0.12).setFill()
        glow.fill()
        let secondGlow = NSBezierPath(ovalIn: NSRect(x: bounds.width - 390, y: -80, width: 510, height: 410))
        Palette.healthy.withAlpha(0.07).setFill()
        secondGlow.fill()
    }

    private func drawHeader() {
        let left: CGFloat = 32
        drawText(
            Copy.tokenDetails(language),
            in: NSRect(x: left, y: 46, width: bounds.width - 64, height: 34),
            font: Typography.system(27, weight: .bold),
            color: Palette.text
        )
        drawText(
            Copy.localDisclosure(language),
            in: NSRect(x: left, y: 82, width: bounds.width - 64, height: 22),
            font: Typography.system(11.5),
            color: Palette.secondary
        )
        let sampled = DateFormatter.localizedString(
            from: snapshot.sampledAt,
            dateStyle: .none,
            timeStyle: .medium
        )
        drawText(
            (language == .chinese ? "本地更新 " : "LOCAL UPDATE ") + sampled,
            in: NSRect(x: bounds.width - 250, y: 53, width: 218, height: 18),
            font: Typography.mono(9.5, weight: .medium),
            color: Palette.secondary,
            alignment: .right
        )
    }

    private func drawMetrics() {
        let left: CGFloat = 32
        let gap: CGFloat = 16
        let width = (bounds.width - left * 2 - gap * 3) / 4
        let top: CGFloat = 126
        let height: CGFloat = 112
        let values: [(String, Int64, String, NSColor)] = [
            (
                language == .chinese ? "本机记录累计" : "LOCAL TOTAL",
                snapshot.totalTokens,
                snapshot.since.map {
                    (language == .chinese ? "自 " : "SINCE ") +
                    DateFormatter.localizedString(from: $0, dateStyle: .medium, timeStyle: .none)
                } ?? "—",
                Palette.accent
            ),
            (
                language == .chinese ? "今日消耗" : "TODAY",
                snapshot.todayTokens,
                language == .chinese ? "按本地自然日统计" : "LOCAL CALENDAR DAY",
                Palette.healthy
            ),
            (
                language == .chinese ? "本月" : "THIS MONTH",
                snapshot.monthTokens,
                language == .chinese ? "按当前自然月统计" : "CURRENT CALENDAR MONTH",
                NSColor(calibratedRed: 105 / 255, green: 86 / 255, blue: 205 / 255, alpha: 1)
            ),
            (
                language == .chinese ? "本周" : "THIS WEEK",
                snapshot.weekTokens,
                language == .chinese ? "按当前自然周统计" : "CURRENT CALENDAR WEEK",
                NSColor(calibratedRed: 49 / 255, green: 174 / 255, blue: 190 / 255, alpha: 1)
            )
        ]
        for (index, item) in values.enumerated() {
            let rect = NSRect(x: left + CGFloat(index) * (width + gap), y: top, width: width, height: height)
            drawMetricCard(rect, title: item.0, value: item.1, note: item.2, accent: item.3, featured: index == 0)
        }
    }

    private func drawMetricCard(
        _ rect: NSRect,
        title: String,
        value: Int64,
        note: String,
        accent: NSColor,
        featured: Bool
    ) {
        let path = roundedPath(rect, radius: 18)
        (featured ? NSColor.white.withAlpha(0.74) : NSColor.white.withAlpha(0.66)).setFill()
        path.fill()
        NSColor.white.withAlpha(0.88).setStroke()
        path.lineWidth = 0.8
        path.stroke()

        accent.setFill()
        NSBezierPath(ovalIn: NSRect(x: rect.minX + 20, y: rect.minY + 20, width: 7, height: 7)).fill()
        drawText(
            title,
            in: NSRect(x: rect.minX + 34, y: rect.minY + 14, width: rect.width - 52, height: 20),
            font: Typography.system(11.5, weight: .medium),
            color: Palette.secondary
        )
        drawText(
            formatTokens(value, language: language),
            in: NSRect(x: rect.minX + 20, y: rect.minY + 40, width: rect.width - 40, height: 36),
            font: Typography.mono(27, weight: .semibold),
            color: Palette.text
        )
        drawText(
            note,
            in: NSRect(x: rect.minX + 20, y: rect.maxY - 29, width: rect.width - 40, height: 17),
            font: Typography.system(10),
            color: Palette.secondary
        )
    }

    private func drawContextCapacity() {
        let rect = NSRect(x: 32, y: 260, width: bounds.width - 64, height: 154)
        contextRect = rect
        let card = roundedPath(rect, radius: 20)
        NSGradient(colors: [
            NSColor(calibratedRed: 239 / 255, green: 248 / 255, blue: 253 / 255, alpha: 0.94),
            NSColor(calibratedRed: 247 / 255, green: 250 / 255, blue: 246 / 255, alpha: 0.94)
        ])?.draw(in: card, angle: -8)
        NSColor.white.withAlpha(0.9).setStroke()
        card.lineWidth = 0.8
        card.stroke()

        let remainingPercent = quota.available
            ? quota.weeklyRemaining.map { min(100, max(0, $0)) }
            : nil
        let usedPercent = remainingPercent.map { min(100, max(0, 100 - $0)) }
        let state = quotaStateColor(remainingPercent)
        let heroWidth = max(260, rect.width * 0.34)
        drawText(
            language == .chinese ? "本周总用量" : "WEEKLY TOTAL USAGE",
            in: NSRect(x: rect.minX + 24, y: rect.minY + 17, width: heroWidth - 40, height: 24),
            font: Typography.system(16, weight: .semibold),
            color: Palette.text
        )
        drawText(
            language == .chinese ? "点击查看容量与占用明细" : "CLICK FOR CAPACITY AND USAGE DETAILS",
            in: NSRect(x: rect.maxX - 276, y: rect.minY + 20, width: 252, height: 17),
            font: Typography.system(9.5, weight: .medium),
            color: hoveringContext ? Palette.accent : Palette.secondary,
            alignment: .right
        )
        drawText(
            usedPercent.map { String(format: "%.0f%%", $0) } ?? "—",
            in: NSRect(x: rect.minX + 22, y: rect.minY + 49, width: 92, height: 42),
            font: Typography.mono(32, weight: .semibold),
            color: state
        )
        let ratio: String
        if let usedPercent, let remainingPercent {
            ratio = language == .chinese
                ? "已用 \(formatPercent(usedPercent)) · 剩余 \(formatPercent(remainingPercent))"
                : "USED \(formatPercent(usedPercent)) · REMAINING \(formatPercent(remainingPercent))"
        } else {
            ratio = language == .chinese ? "等待官方配额数据" : "WAITING FOR OFFICIAL QUOTA"
        }
        drawText(
            ratio,
            in: NSRect(x: rect.minX + 104, y: rect.minY + 65, width: heroWidth - 126, height: 18),
            font: Typography.mono(10.5, weight: .medium),
            color: Palette.secondary
        )
        drawText(
            language == .chinese ? "本周已用" : "WEEKLY USED",
            in: NSRect(x: rect.minX + 24, y: rect.minY + 104, width: 76, height: 16),
            font: Typography.system(9.5, weight: .medium),
            color: Palette.secondary
        )

        let track = NSRect(x: rect.minX + 104, y: rect.minY + 108, width: max(110, heroWidth - 128), height: 7)
        Palette.stroke.withAlpha(0.52).setFill()
        roundedPath(track, radius: 3.5).fill()
        if let usedPercent {
            let fill = NSRect(
                x: track.minX,
                y: track.minY,
                width: max(4, min(track.width, track.width * usedPercent / 100)),
                height: track.height
            )
            state.setFill()
            roundedPath(fill, radius: 3.5).fill()
        }
        drawText(
            quotaGuidance(remainingPercent),
            in: NSRect(x: rect.minX + 24, y: rect.minY + 127, width: heroWidth - 42, height: 17),
            font: Typography.system(9.5, weight: .medium),
            color: state
        )

        let detailsX = rect.minX + heroWidth + 18
        Palette.stroke.withAlpha(0.5).setStroke()
        let divider = NSBezierPath()
        divider.move(to: NSPoint(x: detailsX - 12, y: rect.minY + 24))
        divider.line(to: NSPoint(x: detailsX - 12, y: rect.maxY - 23))
        divider.lineWidth = 0.8
        divider.stroke()

        let detailsWidth = rect.maxX - 24 - detailsX
        let cellWidth = max(90, detailsWidth / 4)
        drawContextValue(
            x: detailsX,
            y: rect.minY + 38,
            width: cellWidth,
            label: language == .chinese ? "已用配额" : "QUOTA USED",
            value: usedPercent.map { formatPercent($0) } ?? "—"
        )
        drawContextValue(
            x: detailsX + cellWidth,
            y: rect.minY + 38,
            width: cellWidth,
            label: language == .chinese ? "剩余配额" : "QUOTA REMAINING",
            value: remainingPercent.map { formatPercent($0) } ?? "—"
        )
        drawContextValue(
            x: detailsX + cellWidth * 2,
            y: rect.minY + 38,
            width: cellWidth,
            label: language == .chinese ? "重置时间" : "RESET TIME",
            value: formatQuotaReset(quota.weeklyReset)
        )
        drawContextValue(
            x: detailsX + cellWidth * 3,
            y: rect.minY + 38,
            width: max(80, detailsWidth - cellWidth * 3),
            label: language == .chinese ? "账户版本" : "ACCOUNT PLAN",
            value: quota.available ? quota.plan : "—"
        )

        drawText(
            quotaFooter(),
            in: NSRect(x: detailsX, y: rect.maxY - 31, width: detailsWidth, height: 18),
            font: Typography.system(9.5),
            color: Palette.secondary
        )
    }

    private func drawContextValue(x: CGFloat, y: CGFloat, width: CGFloat, label: String, value: String) {
        drawText(
            label,
            in: NSRect(x: x, y: y, width: width - 8, height: 17),
            font: Typography.system(9.5),
            color: Palette.secondary
        )
        drawText(
            value,
            in: NSRect(x: x, y: y + 25, width: width - 8, height: 23),
            font: Typography.mono(14, weight: .semibold),
            color: Palette.text
        )
    }

    private func formatPercent(_ value: Double) -> String {
        String(format: "%.0f%%", min(100, max(0, value)))
    }

    private func formatQuotaReset(_ date: Date?) -> String {
        guard let date else { return "—" }
        let formatter = DateFormatter()
        formatter.locale = language == .chinese ? Locale(identifier: "zh_CN") : Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = language == .chinese ? "M-d HH:mm" : "MMM d HH:mm"
        return formatter.string(from: date)
    }

    private func quotaStateColor(_ remainingPercent: Double?) -> NSColor {
        guard let remainingPercent else { return Palette.secondary.withAlphaComponent(0.62) }
        if remainingPercent >= 50 { return Palette.healthy }
        if remainingPercent >= 10 { return Palette.caution }
        return Palette.critical
    }

    private func quotaGuidance(_ remainingPercent: Double?) -> String {
        guard let remainingPercent else {
            return language == .chinese ? "状态不可用" : "STATUS UNAVAILABLE"
        }
        if remainingPercent >= 50 {
            return language == .chinese
                ? "健康 · 剩余 \(formatPercent(remainingPercent))"
                : "HEALTHY · \(formatPercent(remainingPercent)) REMAINING"
        }
        if remainingPercent >= 10 {
            return language == .chinese
                ? "谨慎 · 剩余 \(formatPercent(remainingPercent))"
                : "CAUTION · \(formatPercent(remainingPercent)) REMAINING"
        }
        return language == .chinese
            ? "紧急 · 剩余 \(formatPercent(remainingPercent))"
            : "CRITICAL · \(formatPercent(remainingPercent)) REMAINING"
    }

    private func quotaFooter() -> String {
        guard quota.available, quota.weeklyRemaining != nil else {
            return language == .chinese
                ? "官方本周配额暂不可用，将自动重试"
                : "OFFICIAL WEEKLY QUOTA UNAVAILABLE · RETRYING AUTOMATICALLY"
        }
        let time = DateFormatter.localizedString(from: quota.sampledAt, dateStyle: .none, timeStyle: .medium)
        if quota.status == "stale" {
            return language == .chinese
                ? "官方配额 · 最近成功数据 \(time) · 当前连接暂不可用"
                : "OFFICIAL QUOTA · LATEST SUCCESSFUL SAMPLE \(time) · CONNECTION UNAVAILABLE"
        }
        return language == .chinese
            ? "官方配额 · 已验证 \(time) · 容量、项目与对话结构请点击查看"
            : "OFFICIAL QUOTA · VERIFIED \(time) · CLICK FOR CAPACITY, PROJECT, AND CONVERSATION DETAILS"
    }

    private func drawActivity() {
        let left: CGFloat = 32
        let top: CGFloat = 438
        let rect = NSRect(x: left, y: top, width: bounds.width - left * 2, height: bounds.height - top - 28)
        let card = roundedPath(rect, radius: 20)
        NSColor.white.withAlpha(0.68).setFill()
        card.fill()
        NSColor.white.withAlpha(0.9).setStroke()
        card.lineWidth = 0.8
        card.stroke()

        drawText(
            language == .chinese ? "Token 活动" : "TOKEN ACTIVITY",
            in: NSRect(x: rect.minX + 24, y: rect.minY + 22, width: 230, height: 24),
            font: Typography.system(16, weight: .semibold),
            color: Palette.text
        )
        drawTabs(in: rect)

        let splitY = rect.minY + min(190, rect.height * 0.55)
        let heatmapRect = NSRect(
            x: rect.minX + 24,
            y: rect.minY + 68,
            width: rect.width - 48,
            height: max(92, splitY - rect.minY - 82)
        )
        drawHeatmap(in: heatmapRect)

        if rect.height >= 280 {
            let trendRect = NSRect(
                x: rect.minX + 24,
                y: splitY + 18,
                width: rect.width - 48,
                height: rect.maxY - splitY - 42
            )
            drawTrend(in: trendRect)
        }
    }

    private func drawTabs(in card: NSRect) {
        let labels: [(TokenDetailView, String)] = [
            (.daily, language == .chinese ? "每日" : "DAILY"),
            (.weekly, language == .chinese ? "每周" : "WEEKLY"),
            (.cumulative, language == .chinese ? "累计" : "CUMULATIVE")
        ]
        let widths: [CGFloat] = language == .chinese ? [54, 54, 54] : [62, 68, 94]
        let total = widths.reduce(0, +)
        var x = card.maxX - 24 - total
        tabRects.removeAll(keepingCapacity: true)
        for (index, item) in labels.enumerated() {
            let rect = NSRect(x: x, y: card.minY + 17, width: widths[index], height: 30)
            tabRects.append((item.0, rect))
            if item.0 == selected {
                Palette.accent.setFill()
                roundedPath(rect, radius: 8).fill()
            } else if item.0 == hoveredTab {
                Palette.stroke.withAlpha(0.38).setFill()
                roundedPath(rect, radius: 8).fill()
            }
            drawText(
                item.1,
                in: rect.insetBy(dx: 5, dy: 8),
                font: Typography.system(10.5, weight: .semibold),
                color: item.0 == selected ? .white : Palette.text,
                alignment: .center
            )
            x += widths[index]
        }
    }

    private func drawHeatmap(in rect: NSRect) {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_US_POSIX")
        calendar.firstWeekday = 2
        let today = calendar.startOfDay(for: Date())
        let rawStart = calendar.date(byAdding: .day, value: -364, to: today) ?? today
        let weekday = calendar.component(.weekday, from: rawStart)
        let offset = (weekday - calendar.firstWeekday + 7) % 7
        let start = calendar.date(byAdding: .day, value: -offset, to: rawStart) ?? rawStart
        let values = Dictionary(uniqueKeysWithValues: snapshot.days.map { (calendar.startOfDay(for: $0.day), $0.tokens) })
        let maxValue = max(1, values.values.max() ?? 1)
        let columns = max(1, calendar.dateComponents([.weekOfYear], from: start, to: today).weekOfYear ?? 52)
        let gap: CGFloat = 3
        let labelWidth: CGFloat = 34
        let cell = max(5, min(13, (rect.width - labelWidth - CGFloat(columns) * gap) / CGFloat(columns + 1)))
        let gridX = rect.minX + labelWidth
        let gridY = rect.minY + 18

        let weekdayLabels = language == .chinese ? ["一", "三", "五", "日"] : ["M", "W", "F", "S"]
        for (index, label) in weekdayLabels.enumerated() {
            drawText(
                label,
                in: NSRect(x: rect.minX, y: gridY + CGFloat(index * 2) * (cell + gap) - 1, width: 20, height: 12),
                font: Typography.system(8),
                color: Palette.secondary
            )
        }

        for week in 0 ... columns {
            for day in 0 ..< 7 {
                guard let date = calendar.date(byAdding: .day, value: week * 7 + day, to: start), date <= today else {
                    continue
                }
                let value = values[date] ?? 0
                let intensity = value == 0 ? 0 : min(1, log(Double(value) + 1) / log(Double(maxValue) + 1))
                let color = heatColor(intensity)
                let cellRect = NSRect(
                    x: gridX + CGFloat(week) * (cell + gap),
                    y: gridY + CGFloat(day) * (cell + gap),
                    width: cell,
                    height: cell
                )
                color.setFill()
                roundedPath(cellRect, radius: min(3, cell * 0.25)).fill()
            }
        }
        drawText(
            language == .chinese ? "较少" : "LESS",
            in: NSRect(x: rect.maxX - 134, y: rect.maxY - 14, width: 32, height: 12),
            font: Typography.system(8),
            color: Palette.secondary
        )
        for index in 0 ..< 5 {
            heatColor(Double(index) / 4).setFill()
            roundedPath(NSRect(x: rect.maxX - 98 + CGFloat(index) * 17, y: rect.maxY - 14, width: 11, height: 11), radius: 2).fill()
        }
    }

    private func heatColor(_ value: Double) -> NSColor {
        if value <= 0 { return NSColor(calibratedRed: 234 / 255, green: 240 / 255, blue: 245 / 255, alpha: 1) }
        if value < 0.25 { return NSColor(calibratedRed: 205 / 255, green: 225 / 255, blue: 244 / 255, alpha: 1) }
        if value < 0.5 { return Palette.accentSoft }
        if value < 0.75 { return NSColor(calibratedRed: 91 / 255, green: 153 / 255, blue: 231 / 255, alpha: 1) }
        return Palette.accent
    }

    private func drawTrend(in rect: NSRect) {
        let series = trendSeries()
        let title: String
        switch selected {
        case .daily: title = language == .chinese ? "近 30 日" : "LAST 30 DAYS"
        case .weekly: title = language == .chinese ? "近 16 周" : "LAST 16 WEEKS"
        case .cumulative: title = language == .chinese ? "累计走势" : "CUMULATIVE TREND"
        }
        drawText(
            title,
            in: NSRect(x: rect.minX, y: rect.minY, width: 180, height: 16),
            font: Typography.system(10.5, weight: .semibold),
            color: Palette.secondary
        )
        let chart = NSRect(x: rect.minX, y: rect.minY + 23, width: rect.width, height: max(40, rect.height - 23))
        let grid = NSBezierPath()
        for index in 0 ... 3 {
            let y = chart.minY + CGFloat(index) * chart.height / 3
            grid.move(to: NSPoint(x: chart.minX, y: y))
            grid.line(to: NSPoint(x: chart.maxX, y: y))
        }
        Palette.stroke.withAlpha(0.32).setStroke()
        grid.lineWidth = 0.6
        grid.stroke()
        guard series.count >= 2 else { return }
        let maxValue = max(Int64(1), series.max() ?? 1)
        let line = NSBezierPath()
        var points: [NSPoint] = []
        for (index, value) in series.enumerated() {
            let x = chart.minX + CGFloat(index) / CGFloat(series.count - 1) * chart.width
            let y = chart.maxY - CGFloat(Double(value) / Double(maxValue)) * chart.height
            let point = NSPoint(x: x, y: y)
            points.append(point)
            if index == 0 {
                line.move(to: point)
            } else {
                line.line(to: point)
            }
        }
        guard let fill = line.copy() as? NSBezierPath else { return }
        fill.line(to: NSPoint(x: chart.maxX, y: chart.maxY))
        fill.line(to: NSPoint(x: chart.minX, y: chart.maxY))
        fill.close()
        NSGradient(
            starting: Palette.accent.withAlpha(0.22),
            ending: Palette.accent.withAlpha(0.01)
        )?.draw(in: fill, angle: 90)
        Palette.accent.setStroke()
        line.lineWidth = 2
        line.lineJoinStyle = .round
        line.lineCapStyle = .round
        line.stroke()
        if let last = points.last {
            Palette.surface.setFill()
            NSBezierPath(ovalIn: NSRect(x: last.x - 3.5, y: last.y - 3.5, width: 7, height: 7)).fill()
            Palette.accent.setStroke()
            let point = NSBezierPath(ovalIn: NSRect(x: last.x - 3.5, y: last.y - 3.5, width: 7, height: 7))
            point.lineWidth = 2
            point.stroke()
        }
    }

    private func trendSeries() -> [Int64] {
        var calendar = Calendar(identifier: .gregorian)
        calendar.firstWeekday = 2
        let today = calendar.startOfDay(for: Date())
        let daily = Dictionary(uniqueKeysWithValues: snapshot.days.map { (calendar.startOfDay(for: $0.day), $0.tokens) })
        switch selected {
        case .daily:
            return (-29 ... 0).map { offset in
                guard let date = calendar.date(byAdding: .day, value: offset, to: today) else { return 0 }
                return daily[date] ?? 0
            }
        case .weekly:
            let start = calendar.dateInterval(of: .weekOfYear, for: today)?.start ?? today
            return (-15 ... 0).map { offset in
                guard
                    let week = calendar.date(byAdding: .weekOfYear, value: offset, to: start),
                    let end = calendar.date(byAdding: .day, value: 7, to: week)
                else { return 0 }
                return daily.filter { $0.key >= week && $0.key < end }.values.reduce(0, +)
            }
        case .cumulative:
            var running: Int64 = 0
            return snapshot.days.map {
                running += $0.tokens
                return running
            }
        }
    }
}
