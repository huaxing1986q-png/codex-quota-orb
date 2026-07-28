import AppKit

private enum ContextBreakdownSection: Int {
    case capacity
    case projects
    case conversations
}

final class ContextBreakdownWindowController: NSWindowController, NSWindowDelegate {
    private let breakdownView: ContextBreakdownView
    var onReturn: (() -> Void)?

    init(snapshot: TokenHistorySnapshot, language: AppLanguage) {
        breakdownView = ContextBreakdownView(frame: NSRect(x: 0, y: 0, width: 780, height: 560))
        breakdownView.snapshot = snapshot
        breakdownView.language = language
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 780, height: 560),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.contentView = breakdownView
        window.minSize = NSSize(width: 680, height: 500)
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.isReleasedWhenClosed = false
        window.backgroundColor = Palette.canvas
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        super.init(window: window)
        window.delegate = self
        breakdownView.onBack = { [weak window] in window?.performClose(nil) }
        update(snapshot: snapshot, language: language)
    }

    required init?(coder: NSCoder) {
        nil
    }

    func update(snapshot: TokenHistorySnapshot, language: AppLanguage) {
        breakdownView.snapshot = snapshot
        breakdownView.language = language
        window?.title = language == .chinese ? "上下文容量分布" : "Context capacity breakdown"
        breakdownView.needsDisplay = true
    }

    func present(relativeTo parent: NSWindow?) {
        guard let window else { return }
        if !window.isVisible {
            if let parent {
                let parentFrame = parent.frame
                let origin = NSPoint(
                    x: parentFrame.midX - window.frame.width / 2,
                    y: parentFrame.midY - window.frame.height / 2
                )
                window.setFrameOrigin(origin)
            } else {
                window.center()
            }
        }
        if window.isMiniaturized { window.deminiaturize(nil) }
        NSApp.activate(ignoringOtherApps: true)
        showWindow(nil)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(breakdownView)
    }

    func closeForParent() {
        window?.orderOut(nil)
    }

    func windowWillClose(_ notification: Notification) {
        onReturn?()
    }
}

private final class ContextBreakdownView: NSView {
    var snapshot = TokenHistorySnapshot()
    var language: AppLanguage = .systemDefault
    var onBack: (() -> Void)?

    private let backRect = NSRect(x: 28, y: 22, width: 78, height: 34)
    private var tabRects: [(ContextBreakdownSection, NSRect)] = []
    private var trackingReference: NSTrackingArea?
    private var hoveringBack = false
    private var hoveredTab: ContextBreakdownSection?
    private var selected: ContextBreakdownSection = .capacity
    private var scrollOffset = 0

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
        let next = backRect.contains(point)
        let nextTab = tabRects.first { $0.1.contains(point) }?.0
        if next != hoveringBack {
            hoveringBack = next
            needsDisplay = true
        }
        if nextTab != hoveredTab {
            hoveredTab = nextTab
            needsDisplay = true
        }
        (next || nextTab != nil ? NSCursor.pointingHand : NSCursor.arrow).set()
    }

    override func mouseExited(with event: NSEvent) {
        hoveringBack = false
        hoveredTab = nil
        NSCursor.arrow.set()
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if backRect.contains(point) {
            onBack?()
            return
        }
        if let tab = tabRects.first(where: { $0.1.contains(point) })?.0 {
            selected = tab
            scrollOffset = 0
            needsDisplay = true
        }
    }

    override func scrollWheel(with event: NSEvent) {
        guard selected != .capacity else {
            super.scrollWheel(with: event)
            return
        }
        let count = selected == .projects ? snapshot.projects.count : snapshot.conversations.count
        let visibleRows = max(1, Int((bounds.height - 242) / 46))
        let maximum = max(0, count - visibleRows)
        if event.scrollingDeltaY < 0 {
            scrollOffset = min(maximum, scrollOffset + max(1, Int(abs(event.scrollingDeltaY) / 8)))
        } else if event.scrollingDeltaY > 0 {
            scrollOffset = max(0, scrollOffset - max(1, Int(abs(event.scrollingDeltaY) / 8)))
        }
        needsDisplay = true
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 {
            onBack?()
        } else {
            super.keyDown(with: event)
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .high
        drawBackground()
        drawBackButton()
        drawHeader()
        drawTabs()
        drawPanel()
    }

    private func drawBackground() {
        NSGradient(colors: [
            NSColor(calibratedRed: 232 / 255, green: 244 / 255, blue: 251 / 255, alpha: 1),
            NSColor(calibratedWhite: 0.985, alpha: 1),
            NSColor(calibratedRed: 244 / 255, green: 247 / 255, blue: 247 / 255, alpha: 1)
        ])?.draw(in: bounds, angle: -14)
    }

    private func drawBackButton() {
        let path = roundedPath(backRect, radius: 9)
        (hoveringBack ? Palette.stroke.withAlpha(0.46) : NSColor.white.withAlpha(0.65)).setFill()
        path.fill()
        Palette.stroke.withAlpha(0.75).setStroke()
        path.lineWidth = 0.8
        path.stroke()
        drawText(
            language == .chinese ? "返回" : "BACK",
            in: backRect.insetBy(dx: 7, dy: 9),
            font: Typography.system(10.5, weight: .semibold),
            color: Palette.text,
            alignment: .center
        )
    }

    private func drawHeader() {
        drawText(
            language == .chinese ? "容量与占用明细" : "CAPACITY AND USAGE DETAILS",
            in: NSRect(x: 126, y: 23, width: bounds.width - 154, height: 31),
            font: Typography.system(24, weight: .bold),
            color: Palette.text
        )
        let subtitle: String
        if selected == .projects {
            subtitle = language == .chinese
                ? "按项目路径聚合所有本机会话，并显示每个项目的 Token 占比"
                : "All local sessions grouped by project path with each project's Token share"
        } else if selected == .conversations {
            subtitle = language == .chinese
                ? "按 Token 从大到小列出每个对话；暖色表示大容量对话"
                : "Every conversation sorted by Token volume; warm colors flag large conversations"
        } else {
            subtitle = language == .chinese
                ? "容量只拆分为缓存输入、新增输入与剩余空间，三者不会重复计算"
                : "Capacity is split into cached input, fresh input, and remaining space without double counting"
        }
        drawText(
            subtitle,
            in: NSRect(x: 126, y: 59, width: bounds.width - 154, height: 20),
            font: Typography.system(10.5),
            color: Palette.secondary
        )
    }

    private func drawTabs() {
        let labels: [(ContextBreakdownSection, String)] = [
            (.capacity, language == .chinese ? "容量结构" : "CAPACITY"),
            (.projects, language == .chinese ? "项目占比" : "PROJECTS"),
            (.conversations, language == .chinese ? "对话明细" : "CONVERSATIONS")
        ]
        let widths: [CGFloat] = language == .chinese ? [96, 96, 96] : [102, 104, 132]
        let gap: CGFloat = 6
        let total = widths.reduce(0, +) + gap * 2
        var x = bounds.maxX - 28 - total
        tabRects.removeAll(keepingCapacity: true)
        for (index, item) in labels.enumerated() {
            let rect = NSRect(x: x, y: 94, width: widths[index], height: 34)
            tabRects.append((item.0, rect))
            if item.0 == selected {
                Palette.accent.setFill()
            } else if item.0 == hoveredTab {
                Palette.stroke.withAlpha(0.46).setFill()
            } else {
                NSColor.white.withAlpha(0.62).setFill()
            }
            roundedPath(rect, radius: 9).fill()
            drawText(
                item.1,
                in: rect.insetBy(dx: 6, dy: 10),
                font: Typography.system(10, weight: item.0 == selected ? .semibold : .regular),
                color: item.0 == selected ? .white : Palette.text,
                alignment: .center
            )
            x += widths[index] + gap
        }
    }

    private func drawPanel() {
        let panel = NSRect(x: 28, y: 140, width: bounds.width - 56, height: bounds.height - 166)
        let path = roundedPath(panel, radius: 20)
        NSColor.white.withAlpha(0.72).setFill()
        path.fill()
        NSColor.white.withAlpha(0.92).setStroke()
        path.lineWidth = 0.8
        path.stroke()

        if selected == .projects {
            drawProjects(in: panel)
            return
        }
        if selected == .conversations {
            drawConversations(in: panel)
            return
        }

        let context = snapshot.context
        guard context.available, context.capacityTokens > 0 else {
            drawText(
                language == .chinese ? "当前活动会话尚无可用的上下文容量记录" : "No context capacity record is available for the active session",
                in: NSRect(x: panel.minX + 40, y: panel.midY - 14, width: panel.width - 80, height: 28),
                font: Typography.system(15, weight: .semibold),
                color: Palette.secondary,
                alignment: .center
            )
            return
        }

        let capacity = context.capacityTokens
        let cached = min(capacity, max(0, context.cachedInputTokens))
        let fresh = min(max(0, capacity - cached), max(0, context.freshInputTokens))
        let remaining = max(0, capacity - cached - fresh)
        let cachedPercent = Double(cached) * 100 / Double(capacity)
        let freshPercent = Double(fresh) * 100 / Double(capacity)
        let remainingPercent = Double(remaining) * 100 / Double(capacity)
        let cachedColor = Palette.accent
        let freshColor = NSColor(calibratedRed: 112 / 255, green: 87 / 255, blue: 205 / 255, alpha: 1)
        let remainingColor = statusColor(remainingPercent)

        let ringRect = NSRect(x: panel.minX + 66, y: panel.minY + 96, width: 174, height: 174)
        drawRing(
            in: ringRect,
            values: [(cachedPercent, cachedColor), (freshPercent, freshColor), (remainingPercent, remainingColor)]
        )
        let percent = String(format: "%.0f%%", context.usedPercent ?? 0)
        drawText(
            percent,
            in: NSRect(x: ringRect.minX + 24, y: ringRect.minY + 54, width: ringRect.width - 48, height: 42),
            font: Typography.mono(31, weight: .semibold),
            color: Palette.text,
            alignment: .center
        )
        drawText(
            language == .chinese ? "已占用" : "USED",
            in: NSRect(x: ringRect.minX + 24, y: ringRect.minY + 96, width: ringRect.width - 48, height: 18),
            font: Typography.system(10.5),
            color: Palette.secondary,
            alignment: .center
        )
        drawText(
            (language == .chinese ? "总容量 " : "CAPACITY ") + formatTokens(capacity, language: language),
            in: NSRect(x: ringRect.minX - 10, y: ringRect.maxY + 18, width: ringRect.width + 20, height: 18),
            font: Typography.mono(10),
            color: Palette.secondary,
            alignment: .center
        )

        let rightX = panel.minX + 306
        let rightWidth = panel.maxX - rightX - 30
        drawText(
            language == .chinese ? "容量构成" : "CAPACITY STRUCTURE",
            in: NSRect(x: rightX, y: panel.minY + 34, width: rightWidth, height: 24),
            font: Typography.system(16, weight: .semibold),
            color: Palette.text
        )
        drawStackedBar(
            in: NSRect(x: rightX, y: panel.minY + 72, width: rightWidth, height: 14),
            values: [(cachedPercent, cachedColor), (freshPercent, freshColor), (remainingPercent, remainingColor)]
        )
        drawSegment(
            x: rightX,
            y: panel.minY + 110,
            width: rightWidth,
            label: language == .chinese ? "缓存输入" : "CACHED INPUT",
            value: cached,
            percent: cachedPercent,
            color: cachedColor,
            note: language == .chinese ? "属于输入子集" : "SUBSET OF INPUT"
        )
        drawSegment(
            x: rightX,
            y: panel.minY + 174,
            width: rightWidth,
            label: language == .chinese ? "新增输入" : "FRESH INPUT",
            value: fresh,
            percent: freshPercent,
            color: freshColor,
            note: language == .chinese ? "本轮未缓存输入" : "UNCACHED INPUT THIS TURN"
        )
        drawSegment(
            x: rightX,
            y: panel.minY + 238,
            width: rightWidth,
            label: language == .chinese ? "剩余容量" : "REMAINING",
            value: remaining,
            percent: remainingPercent,
            color: remainingColor,
            note: guidance(remainingPercent)
        )

        let footer = NSRect(x: rightX, y: panel.maxY - 92, width: rightWidth, height: 68)
        Palette.stroke.withAlpha(0.22).setFill()
        roundedPath(footer, radius: 12).fill()
        drawText(
            language == .chinese ? "非容量占用指标" : "NOT PART OF CAPACITY OCCUPANCY",
            in: NSRect(x: footer.minX + 16, y: footer.minY + 10, width: footer.width - 32, height: 17),
            font: Typography.system(10.5, weight: .medium),
            color: Palette.text
        )
        let footerText: String
        if language == .chinese {
            footerText = "上轮输出 \(formatTokens(context.outputTokens, language: language))"
                + " · 推理 \(formatTokens(context.reasoningOutputTokens, language: language))（输出子集）"
                + " · 会话累计 \(formatTokens(context.sessionTotalTokens, language: language))"
        } else {
            footerText = "LAST OUTPUT \(formatTokens(context.outputTokens, language: language))"
                + " · REASONING \(formatTokens(context.reasoningOutputTokens, language: language)) (OUTPUT SUBSET)"
                + " · SESSION CUMULATIVE \(formatTokens(context.sessionTotalTokens, language: language))"
        }
        drawText(
            footerText,
            in: NSRect(x: footer.minX + 16, y: footer.minY + 38, width: footer.width - 32, height: 17),
            font: Typography.system(9.5),
            color: Palette.secondary
        )
    }

    private func drawProjects(in panel: NSRect) {
        let content = panel.insetBy(dx: 22, dy: 22)
        let total = max(Int64(1), snapshot.totalTokens)
        let columns: [(String, CGFloat, NSTextAlignment)] = [
            (language == .chinese ? "项目" : "PROJECT", 0.18, .left),
            (language == .chinese ? "项目路径" : "PROJECT PATH", 0.42, .left),
            ("TOKEN", 0.16, .right),
            (language == .chinese ? "总占比" : "TOTAL SHARE", 0.14, .right),
            (language == .chinese ? "对话数" : "CHATS", 0.10, .right)
        ]
        drawTableHeader(columns, in: NSRect(x: content.minX, y: content.minY, width: content.width - 10, height: 28))
        let rowHeight: CGFloat = 46
        let rowsTop = content.minY + 38
        let visible = max(1, Int((content.maxY - rowsTop) / rowHeight))
        let maximum = max(0, snapshot.projects.count - visible)
        scrollOffset = min(maximum, scrollOffset)
        let end = min(snapshot.projects.count, scrollOffset + visible)
        for index in scrollOffset ..< end {
            let project = snapshot.projects[index]
            let row = NSRect(
                x: content.minX,
                y: rowsTop + CGFloat(index - scrollOffset) * rowHeight,
                width: content.width - 10,
                height: rowHeight - 4
            )
            if index % 2 == 0 {
                Palette.stroke.withAlpha(0.12).setFill()
                roundedPath(row, radius: 8).fill()
            }
            let share = Double(project.tokens) * 100 / Double(total)
            let color = share >= 25 ? Palette.critical : (share >= 10 ? Palette.caution : Palette.text)
            let values = [
                project.projectName,
                project.projectPath ?? "—",
                formatTokens(project.tokens, language: language),
                String(format: "%.1f%%", share),
                "\(project.conversations)"
            ]
            drawTableRow(values, columns: columns, in: row, color: color)
            Palette.accent.withAlpha(0.58).setFill()
            NSBezierPath(rect: NSRect(x: row.minX, y: row.maxY - 2, width: row.width * CGFloat(min(100, share)) / 100, height: 2)).fill()
        }
        drawScrollIndicator(count: snapshot.projects.count, visible: visible, offset: scrollOffset, in: content)
    }

    private func drawConversations(in panel: NSRect) {
        let content = panel.insetBy(dx: 22, dy: 22)
        let total = max(Int64(1), snapshot.totalTokens)
        let projectTotals = Dictionary(uniqueKeysWithValues: snapshot.projects.map {
            (($0.projectPath ?? "(unknown)"), $0.tokens)
        })
        let columns: [(String, CGFloat, NSTextAlignment)] = [
            (language == .chinese ? "对话" : "CONVERSATION", 0.24, .left),
            (language == .chinese ? "项目" : "PROJECT", 0.18, .left),
            ("TOKEN", 0.16, .right),
            (language == .chinese ? "总占比" : "TOTAL", 0.12, .right),
            (language == .chinese ? "项目内" : "PROJECT", 0.13, .right),
            (language == .chinese ? "最后活动" : "UPDATED", 0.17, .right)
        ]
        drawTableHeader(columns, in: NSRect(x: content.minX, y: content.minY, width: content.width - 10, height: 28))
        let rowHeight: CGFloat = 46
        let rowsTop = content.minY + 38
        let visible = max(1, Int((content.maxY - rowsTop) / rowHeight))
        let maximum = max(0, snapshot.conversations.count - visible)
        scrollOffset = min(maximum, scrollOffset)
        let end = min(snapshot.conversations.count, scrollOffset + visible)
        for index in scrollOffset ..< end {
            let conversation = snapshot.conversations[index]
            let row = NSRect(
                x: content.minX,
                y: rowsTop + CGFloat(index - scrollOffset) * rowHeight,
                width: content.width - 10,
                height: rowHeight - 4
            )
            if index % 2 == 0 {
                Palette.stroke.withAlpha(0.12).setFill()
                roundedPath(row, radius: 8).fill()
            }
            let totalShare = Double(conversation.tokens) * 100 / Double(total)
            let projectTotal = max(Int64(1), projectTotals[conversation.projectPath ?? "(unknown)"] ?? conversation.tokens)
            let projectShare = Double(conversation.tokens) * 100 / Double(projectTotal)
            let shortID = String(conversation.sessionID.prefix(8))
            let started = DateFormatter.localizedString(from: conversation.startedAt, dateStyle: .short, timeStyle: .short)
            let updated = DateFormatter.localizedString(from: conversation.updatedAt, dateStyle: .short, timeStyle: .short)
            let color = totalShare >= 10 ? Palette.critical : (totalShare >= 5 ? Palette.caution : Palette.text)
            let values = [
                started + " · " + shortID,
                conversation.projectName,
                formatTokens(conversation.tokens, language: language),
                String(format: "%.1f%%", totalShare),
                String(format: "%.1f%%", projectShare),
                updated
            ]
            drawTableRow(values, columns: columns, in: row, color: color)
            color.withAlpha(0.56).setFill()
            NSBezierPath(rect: NSRect(x: row.minX, y: row.maxY - 2, width: row.width * CGFloat(min(100, totalShare)) / 100, height: 2)).fill()
        }
        drawScrollIndicator(count: snapshot.conversations.count, visible: visible, offset: scrollOffset, in: content)
    }

    private func drawTableHeader(
        _ columns: [(String, CGFloat, NSTextAlignment)],
        in rect: NSRect
    ) {
        var x = rect.minX
        for column in columns {
            let width = rect.width * column.1
            drawText(
                column.0,
                in: NSRect(x: x + 8, y: rect.minY + 7, width: width - 16, height: 16),
                font: Typography.system(9, weight: .semibold),
                color: Palette.secondary,
                alignment: column.2
            )
            x += width
        }
        Palette.stroke.withAlpha(0.4).setFill()
        NSBezierPath(rect: NSRect(x: rect.minX, y: rect.maxY - 1, width: rect.width, height: 1)).fill()
    }

    private func drawTableRow(
        _ values: [String],
        columns: [(String, CGFloat, NSTextAlignment)],
        in rect: NSRect,
        color: NSColor
    ) {
        var x = rect.minX
        for index in 0 ..< min(values.count, columns.count) {
            let width = rect.width * columns[index].1
            drawText(
                values[index],
                in: NSRect(x: x + 8, y: rect.minY + 12, width: width - 16, height: 18),
                font: index == 2 ? Typography.mono(10.5, weight: .semibold) : Typography.system(10),
                color: color,
                alignment: columns[index].2
            )
            x += width
        }
    }

    private func drawScrollIndicator(count: Int, visible: Int, offset: Int, in rect: NSRect) {
        guard count > visible, visible > 0 else { return }
        let track = NSRect(x: rect.maxX - 4, y: rect.minY + 38, width: 3, height: rect.height - 44)
        Palette.stroke.withAlpha(0.32).setFill()
        roundedPath(track, radius: 1.5).fill()
        let thumbHeight = max(24, track.height * CGFloat(visible) / CGFloat(count))
        let maximum = max(1, count - visible)
        let thumbY = track.minY + (track.height - thumbHeight) * CGFloat(offset) / CGFloat(maximum)
        Palette.accent.withAlpha(0.72).setFill()
        roundedPath(NSRect(x: track.minX, y: thumbY, width: track.width, height: thumbHeight), radius: 1.5).fill()
    }

    private func drawRing(in rect: NSRect, values: [(Double, NSColor)]) {
        Palette.stroke.withAlpha(0.42).setStroke()
        let track = NSBezierPath(ovalIn: rect)
        track.lineWidth = 24
        track.stroke()
        var start: CGFloat = 90
        for (percent, color) in values where percent > 0 {
            let end = start - CGFloat(percent * 3.6)
            let arc = NSBezierPath()
            arc.appendArc(
                withCenter: NSPoint(x: rect.midX, y: rect.midY),
                radius: rect.width / 2,
                startAngle: start,
                endAngle: end,
                clockwise: true
            )
            arc.lineWidth = 24
            arc.lineCapStyle = .butt
            color.setStroke()
            arc.stroke()
            start = end
        }
    }

    private func drawStackedBar(in rect: NSRect, values: [(Double, NSColor)]) {
        Palette.stroke.withAlpha(0.42).setFill()
        roundedPath(rect, radius: 7).fill()
        var x = rect.minX
        for (percent, color) in values {
            let width = rect.width * CGFloat(max(0, percent)) / 100
            guard width > 0 else { continue }
            color.setFill()
            NSBezierPath(rect: NSRect(x: x, y: rect.minY, width: width, height: rect.height)).fill()
            x += width
        }
    }

    private func drawSegment(
        x: CGFloat,
        y: CGFloat,
        width: CGFloat,
        label: String,
        value: Int64,
        percent: Double,
        color: NSColor,
        note: String
    ) {
        color.setFill()
        NSBezierPath(rect: NSRect(x: x, y: y, width: 4, height: 42)).fill()
        drawText(
            label,
            in: NSRect(x: x + 16, y: y, width: width * 0.46, height: 18),
            font: Typography.system(10.5),
            color: Palette.text
        )
        drawText(
            formatTokens(value, language: language) + " · " + String(format: "%.1f%%", percent),
            in: NSRect(x: x + width * 0.45, y: y - 3, width: width * 0.55, height: 23),
            font: Typography.mono(14, weight: .semibold),
            color: Palette.text,
            alignment: .right
        )
        drawText(
            note,
            in: NSRect(x: x + 16, y: y + 25, width: width - 16, height: 16),
            font: Typography.system(9),
            color: Palette.secondary
        )
    }

    private func statusColor(_ remainingPercent: Double) -> NSColor {
        if remainingPercent >= 50 { return Palette.healthy }
        if remainingPercent >= 10 { return Palette.caution }
        return Palette.critical
    }

    private func guidance(_ remainingPercent: Double) -> String {
        if remainingPercent >= 50 {
            return language == .chinese ? "健康，暂无需整理" : "HEALTHY, NO CLEANUP NEEDED"
        }
        if remainingPercent >= 10 {
            return language == .chinese ? "谨慎，建议整理无关上下文" : "CAUTION, TRIM UNRELATED CONTEXT"
        }
        return language == .chinese ? "紧急，建议总结后新建任务" : "CRITICAL, SUMMARIZE AND START A NEW TASK"
    }
}
