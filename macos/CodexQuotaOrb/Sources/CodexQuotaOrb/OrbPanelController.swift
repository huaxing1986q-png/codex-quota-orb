import AppKit
import QuartzCore

private final class OverlayPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

protocol OrbPanelActions: AnyObject {
    func orbPanelDidRequestToggle(_ controller: OrbPanelController)
    func orbPanelDidRequestDetails(_ controller: OrbPanelController)
    func orbPanelDidRequestLanguage(_ controller: OrbPanelController)
    func orbPanelDidRequestPin(_ controller: OrbPanelController)
    func orbPanel(_ controller: OrbPanelController, didMoveAnchor anchor: NSPoint)
}

final class OrbPanelController {
    static let orbSize = NSSize(width: 48, height: 48)
    static let cardSize = NSSize(width: 252, height: 132)

    let panel: NSPanel
    weak var actions: OrbPanelActions?
    private let content: OrbView
    private(set) var isExpanded = false
    private(set) var anchor: NSPoint
    private(set) var openedAt = Date.distantPast
    private var visible = false

    init(snapshot: QuotaSnapshot, preferences: WidgetPreferences) {
        let initialAnchor = Self.initialAnchor(preferences)
        anchor = initialAnchor
        let frame = NSRect(origin: initialAnchor, size: Self.orbSize)
        panel = OverlayPanel(
            contentRect: frame,
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false
        )
        content = OrbView(frame: NSRect(origin: .zero, size: Self.orbSize))
        content.snapshot = snapshot
        content.language = preferences.language
        content.alwaysOnTop = preferences.alwaysOnTop
        panel.contentView = content
        panel.isReleasedWhenClosed = false
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isFloatingPanel = true
        panel.level = preferences.alwaysOnTop ? .statusBar : .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        panel.animationBehavior = .none
        panel.ignoresMouseEvents = false
        panel.acceptsMouseMovedEvents = true
        content.owner = self
    }

    var frame: NSRect { panel.frame }
    var isVisible: Bool { visible }

    func update(snapshot: QuotaSnapshot, preferences: WidgetPreferences) {
        content.snapshot = snapshot
        content.language = preferences.language
        content.alwaysOnTop = preferences.alwaysOnTop
        panel.level = preferences.alwaysOnTop ? .statusBar : .floating
        content.needsDisplay = true
    }

    func show() {
        guard !visible else { return }
        visible = true
        panel.orderFrontRegardless()
    }

    func hide() {
        guard visible else { return }
        visible = false
        isExpanded = false
        panel.orderOut(nil)
        panel.setFrame(NSRect(origin: anchor, size: Self.orbSize), display: false)
        content.isExpanded = false
        content.frame = NSRect(origin: .zero, size: Self.orbSize)
    }

    func setExpanded(_ expanded: Bool, animated: Bool = true) {
        guard expanded != isExpanded else { return }
        isExpanded = expanded
        if expanded { openedAt = Date() }
        content.isExpanded = expanded
        let target = expanded
            ? Self.expandedFrame(for: anchor)
            : NSRect(origin: anchor, size: Self.orbSize)
        content.frame = NSRect(origin: .zero, size: target.size)
        if animated, !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion {
            NSAnimationContext.runAnimationGroup { context in
                context.duration = expanded ? 0.15 : 0.10
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                panel.animator().setFrame(target, display: true)
            }
        } else {
            panel.setFrame(target, display: true)
        }
        content.needsDisplay = true
        if visible { panel.orderFrontRegardless() }
    }

    func moveAnchor(from start: NSPoint, delta: NSPoint) {
        let proposed = NSPoint(x: start.x + delta.x, y: start.y + delta.y)
        anchor = Self.clampedAnchor(proposed)
        let target = isExpanded
            ? Self.expandedFrame(for: anchor)
            : NSRect(origin: anchor, size: Self.orbSize)
        content.frame = NSRect(origin: .zero, size: target.size)
        panel.setFrame(target, display: true)
        actions?.orbPanel(self, didMoveAnchor: anchor)
    }

    fileprivate func toggleFromView() {
        actions?.orbPanelDidRequestToggle(self)
    }

    fileprivate func openDetailsFromView() {
        actions?.orbPanelDidRequestDetails(self)
    }

    fileprivate func changeLanguageFromView() {
        actions?.orbPanelDidRequestLanguage(self)
    }

    fileprivate func togglePinFromView() {
        actions?.orbPanelDidRequestPin(self)
    }

    private static func initialAnchor(_ preferences: WidgetPreferences) -> NSPoint {
        if preferences.hasCustomAnchor {
            return clampedAnchor(NSPoint(x: preferences.anchorX, y: preferences.anchorY))
        }
        let screen = NSScreen.main ?? NSScreen.screens.first
        let visible = screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        return NSPoint(
            x: visible.maxX - orbSize.width - 18,
            y: visible.minY + 18
        )
    }

    private static func clampedAnchor(_ anchor: NSPoint) -> NSPoint {
        let center = NSPoint(x: anchor.x + orbSize.width / 2, y: anchor.y + orbSize.height / 2)
        let screen = screen(containing: center) ?? nearestScreen(to: center) ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(origin: .zero, size: NSScreen.main?.frame.size ?? NSSize(width: 1440, height: 900))
        return NSPoint(
            x: max(visible.minX, min(visible.maxX - orbSize.width, anchor.x)),
            y: max(visible.minY, min(visible.maxY - orbSize.height, anchor.y))
        )
    }

    private static func expandedFrame(for anchor: NSPoint) -> NSRect {
        let orbCenter = NSPoint(x: anchor.x + orbSize.width / 2, y: anchor.y + orbSize.height / 2)
        let screen = screen(containing: orbCenter) ?? nearestScreen(to: orbCenter) ?? NSScreen.main
        let visible = screen?.visibleFrame ?? NSRect(origin: .zero, size: NSSize(width: 1440, height: 900))
        let preferRightAligned = orbCenter.x > visible.midX
        let originX = preferRightAligned ? anchor.x + orbSize.width - cardSize.width : anchor.x
        let originY = min(anchor.y, visible.maxY - cardSize.height)
        return NSRect(origin: NSPoint(x: originX, y: originY), size: cardSize).clamped(to: visible)
    }

    private static func screen(containing point: NSPoint) -> NSScreen? {
        NSScreen.screens.first { $0.frame.contains(point) }
    }

    private static func nearestScreen(to point: NSPoint) -> NSScreen? {
        NSScreen.screens.min {
            distance(point, $0.visibleFrame) < distance(point, $1.visibleFrame)
        }
    }

    private static func distance(_ point: NSPoint, _ rect: NSRect) -> CGFloat {
        let x = max(rect.minX - point.x, 0, point.x - rect.maxX)
        let y = max(rect.minY - point.y, 0, point.y - rect.maxY)
        return hypot(x, y)
    }
}

private final class OrbView: NSView {
    weak var owner: OrbPanelController?
    var snapshot = QuotaSnapshot()
    var language: AppLanguage = .systemDefault
    var alwaysOnTop = false
    var isExpanded = false

    private var downScreen = NSPoint.zero
    private var downAnchor = NSPoint.zero
    private var dragging = false
    private var hovered = false
    private var trackingAreaReference: NSTrackingArea?

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { false }

    override func updateTrackingAreas() {
        if let trackingAreaReference { removeTrackingArea(trackingAreaReference) }
        let area = NSTrackingArea(
            rect: bounds,
            options: [.activeAlways, .mouseEnteredAndExited, .inVisibleRect],
            owner: self,
            userInfo: nil
        )
        addTrackingArea(area)
        trackingAreaReference = area
        super.updateTrackingAreas()
    }

    override func mouseEntered(with event: NSEvent) {
        hovered = true
        needsDisplay = true
    }

    override func mouseExited(with event: NSEvent) {
        hovered = false
        needsDisplay = true
    }

    override func mouseDown(with event: NSEvent) {
        downScreen = NSEvent.mouseLocation
        downAnchor = owner?.anchor ?? .zero
        dragging = false
    }

    override func mouseDragged(with event: NSEvent) {
        let now = NSEvent.mouseLocation
        let delta = NSPoint(x: now.x - downScreen.x, y: now.y - downScreen.y)
        if hypot(delta.x, delta.y) >= 4 { dragging = true }
        if dragging { owner?.moveAnchor(from: downAnchor, delta: delta) }
    }

    override func mouseUp(with event: NSEvent) {
        guard !dragging else { return }
        let point = convert(event.locationInWindow, from: nil)
        if !isExpanded {
            owner?.toggleFromView()
            return
        }
        if languageRect.contains(point) {
            owner?.changeLanguageFromView()
        } else if pinRect.contains(point) {
            owner?.togglePinFromView()
        } else if quotaRect.contains(point) {
            owner?.openDetailsFromView()
        }
    }

    override func draw(_ dirtyRect: NSRect) {
        NSGraphicsContext.current?.imageInterpolation = .high
        if isExpanded { drawCard() } else { drawOrb() }
    }

    private var languageRect: NSRect { NSRect(x: bounds.width - 69, y: 6, width: 31, height: 30) }
    private var pinRect: NSRect { NSRect(x: bounds.width - 37, y: 6, width: 31, height: 30) }
    private var quotaRect: NSRect { NSRect(x: 8, y: 39, width: bounds.width - 16, height: bounds.height - 47) }

    private func drawOrb() {
        let rect = bounds.insetBy(dx: 2.5, dy: 2.5)
        let circle = NSBezierPath(ovalIn: rect)
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlpha(0.18)
        shadow.shadowBlurRadius = 8
        shadow.shadowOffset = NSSize(width: 0, height: -2)
        NSGraphicsContext.saveGraphicsState()
        shadow.set()
        Palette.surface.setFill()
        circle.fill()
        NSGraphicsContext.restoreGraphicsState()

        let glow = Palette.status(snapshot.health).withAlpha(0.12)
        NSGradient(starting: Palette.surface, ending: glow)?.draw(in: circle, angle: -55)
        Palette.stroke.withAlpha(0.9).setStroke()
        circle.lineWidth = 0.8
        circle.stroke()

        let ringRect = rect.insetBy(dx: 2.2, dy: 2.2)
        let track = NSBezierPath(ovalIn: ringRect)
        Palette.stroke.withAlpha(0.55).setStroke()
        track.lineWidth = 2.6
        track.stroke()
        if let remaining = snapshot.weeklyRemaining {
            let arc = NSBezierPath()
            arc.appendArc(
                withCenter: NSPoint(x: ringRect.midX, y: ringRect.midY),
                radius: ringRect.width / 2,
                startAngle: 90,
                endAngle: 90 - CGFloat(remaining / 100) * 360,
                clockwise: true
            )
            arc.lineCapStyle = .round
            Palette.status(snapshot.health).setStroke()
            arc.lineWidth = hovered ? 3.2 : 2.8
            arc.stroke()
        }

        let number = snapshot.weeklyRemaining.map { String(format: "%.0f", $0) } ?? "—"
        drawText(
            number,
            in: NSRect(x: 6, y: 13, width: 29, height: 19),
            font: Typography.mono(14, weight: .medium),
            color: Palette.text,
            alignment: .right
        )
        drawText(
            "%",
            in: NSRect(x: 35, y: 22, width: 8, height: 11),
            font: Typography.system(7, weight: .semibold),
            color: Palette.secondary
        )
    }

    private func drawCard() {
        let card = bounds.insetBy(dx: 0.5, dy: 0.5)
        let path = roundedPath(card, radius: 14)
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlpha(0.17)
        shadow.shadowBlurRadius = 14
        shadow.shadowOffset = NSSize(width: 0, height: -4)
        NSGraphicsContext.saveGraphicsState()
        shadow.set()
        Palette.surface.setFill()
        path.fill()
        NSGraphicsContext.restoreGraphicsState()

        let statusColor = Palette.status(snapshot.health)
        NSGradient(
            colors: [
                Palette.surface,
                statusColor.withAlpha(snapshot.health == .healthy ? 0.08 : 0.14),
                Palette.canvas
            ]
        )?.draw(in: path, angle: 0)
        Palette.stroke.withAlpha(0.9).setStroke()
        path.lineWidth = 0.8
        path.stroke()

        drawText(
            "CODEX · \(snapshot.plan)",
            in: NSRect(x: 13, y: 12, width: 118, height: 16),
            font: Typography.system(10.5, weight: .semibold),
            color: Palette.text
        )
        statusColor.setFill()
        NSBezierPath(ovalIn: NSRect(x: 132, y: 17, width: 5, height: 5)).fill()
        drawText(
            Copy.health(language, value: snapshot.health),
            in: NSRect(x: 141, y: 12, width: 42, height: 16),
            font: Typography.system(9, weight: .medium),
            color: Palette.secondary
        )
        drawText(
            language == .chinese ? "EN" : "中",
            in: languageRect.insetBy(dx: 2, dy: 7),
            font: Typography.system(9.5, weight: .semibold),
            color: Palette.text,
            alignment: .center
        )
        drawText(
            alwaysOnTop ? "↑" : "↟",
            in: pinRect.insetBy(dx: 2, dy: 5),
            font: Typography.system(12, weight: .semibold),
            color: alwaysOnTop ? statusColor : Palette.text,
            alignment: .center
        )

        Palette.stroke.withAlpha(0.75).setStroke()
        let separator = NSBezierPath()
        separator.move(to: NSPoint(x: 13, y: 38))
        separator.line(to: NSPoint(x: bounds.width - 13, y: 38))
        separator.lineWidth = 0.7
        separator.stroke()

        drawText(
            Copy.weekly(language),
            in: NSRect(x: 16, y: 48, width: bounds.width - 32, height: 14),
            font: Typography.system(10, weight: .medium),
            color: Palette.secondary,
            alignment: .center
        )
        let percentage = snapshot.weeklyRemaining.map { String(format: "%.0f%%", $0) } ?? "—"
        drawText(
            percentage,
            in: NSRect(x: 16, y: 62, width: bounds.width - 32, height: 29),
            font: Typography.mono(23, weight: .medium),
            color: Palette.text,
            alignment: .center
        )

        let trackRect = NSRect(x: 18, y: 94, width: bounds.width - 36, height: 5)
        Palette.stroke.withAlpha(0.45).setFill()
        roundedPath(trackRect, radius: 2.5).fill()
        if let remaining = snapshot.weeklyRemaining {
            let fill = NSRect(
                x: trackRect.minX,
                y: trackRect.minY,
                width: max(5, trackRect.width * CGFloat(remaining / 100)),
                height: trackRect.height
            )
            statusColor.setFill()
            roundedPath(fill, radius: 2.5).fill()
        }
        drawText(
            Copy.reset(language, date: snapshot.weeklyReset),
            in: NSRect(x: 16, y: 106, width: bounds.width - 32, height: 14),
            font: Typography.mono(8.5),
            color: Palette.secondary,
            alignment: .center
        )
        if hovered {
            statusColor.withAlpha(0.7).setStroke()
            let hoverPath = roundedPath(quotaRect.insetBy(dx: 2, dy: 2), radius: 9)
            hoverPath.lineWidth = 0.7
            hoverPath.stroke()
        }
    }
}
