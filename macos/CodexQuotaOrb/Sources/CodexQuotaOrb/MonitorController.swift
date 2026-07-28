import AppKit

@MainActor
final class MonitorController: OrbPanelActions {
    private let store = PreferencesStore.shared
    private let quotaService = QuotaService()
    private let historyService = TokenHistoryService()
    private var preferences: WidgetPreferences
    private var quota = QuotaSnapshot()
    private var lastValidQuota: QuotaSnapshot?
    private var history = TokenHistorySnapshot()
    private let orb: OrbPanelController
    private let details: TokenDetailsWindowController
    private var tickTask: Task<Void, Never>?
    private var quotaRequestInFlight = false
    private var historyRequestInFlight = false
    private var lastQuotaRequest = Date.distantPast
    private var nextQuotaRefresh = Date.distantPast
    private var outsideSince: Date?
    private var globalClickMonitor: Any?
    private var localClickMonitor: Any?
    private var saveAnchorTask: Task<Void, Never>?
    private var detailVisible = false

    private lazy var sessionMonitor = SessionChangeMonitor { [weak self] in
        self?.requestSessionsChanged()
    }

    init() {
        preferences = store.load()
        orb = OrbPanelController(snapshot: quota, preferences: preferences)
        details = TokenDetailsWindowController(snapshot: history, language: preferences.language)
        orb.actions = self
        details.onRefresh = { [weak self] in
            self?.requestHistoryRefresh()
        }
        details.onVisibilityChanged = { [weak self] visible in
            self?.requestDetailVisibility(visible)
        }
    }

    func start() {
        installClickMonitors()
        sessionMonitor.start()
        tickTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                if let controller = self {
                    controller.tick()
                } else {
                    return
                }
                try? await Task.sleep(nanoseconds: 250_000_000)
            }
        }
        refreshQuota(force: true)
        refreshHistory()
        tick()
    }

    func stop() {
        tickTask?.cancel()
        tickTask = nil
        saveAnchorTask?.cancel()
        saveAnchorTask = nil
        sessionMonitor.stop()
        if let globalClickMonitor { NSEvent.removeMonitor(globalClickMonitor) }
        if let localClickMonitor { NSEvent.removeMonitor(localClickMonitor) }
        globalClickMonitor = nil
        localClickMonitor = nil
    }

    private func tick() {
        let now = Date()
        if now >= nextQuotaRefresh { refreshQuota(force: false) }
        updateVisibility()
        updatePointerCollapse(now: now)
    }

    private func updateVisibility() {
        let frontmost = NSWorkspace.shared.frontmostApplication
        let name = frontmost?.localizedName?.lowercased() ?? ""
        let bundle = frontmost?.bundleIdentifier?.lowercased() ?? ""
        let codexIsFrontmost = name == "codex" || name.contains("codex") || bundle.contains("codex")
        let finderIsFrontmost = bundle == "com.apple.finder" || name == "finder"
        let shouldShow = preferences.alwaysOnTop
            || codexIsFrontmost
            || detailVisible
            || (orb.isVisible && finderIsFrontmost)

        if shouldShow {
            orb.show()
        } else {
            orb.hide()
            outsideSince = nil
        }
    }

    private func updatePointerCollapse(now: Date) {
        guard orb.isExpanded else {
            outsideSince = nil
            return
        }
        guard now.timeIntervalSince(orb.openedAt) >= 0.4 else { return }
        if orb.frame.contains(NSEvent.mouseLocation) {
            outsideSince = nil
        } else if let outsideSince {
            if now.timeIntervalSince(outsideSince) >= 0.4 {
                orb.setExpanded(false)
                self.outsideSince = nil
            }
        } else {
            outsideSince = now
        }
    }

    private func installClickMonitors() {
        globalClickMonitor = NSEvent.addGlobalMonitorForEvents(
            matching: [.leftMouseDown, .rightMouseDown]
        ) { [weak self] _ in
            self?.requestCollapse()
        }
        localClickMonitor = NSEvent.addLocalMonitorForEvents(
            matching: [.leftMouseDown, .rightMouseDown]
        ) { [weak self] event in
            self?.requestCollapse()
            return event
        }
    }

    private func collapseIfClickIsOutside() {
        guard orb.isExpanded, Date().timeIntervalSince(orb.openedAt) >= 0.12 else { return }
        if !orb.frame.contains(NSEvent.mouseLocation) {
            orb.setExpanded(false)
            outsideSince = nil
        }
    }

    private nonisolated func requestCollapse() {
        Task { @MainActor [weak self] in
            self?.collapseIfClickIsOutside()
        }
    }

    private nonisolated func requestSessionsChanged() {
        Task { @MainActor [weak self] in
            self?.sessionsChanged()
        }
    }

    private nonisolated func requestHistoryRefresh() {
        Task { @MainActor [weak self] in
            self?.refreshHistory()
        }
    }

    private nonisolated func requestDetailVisibility(_ visible: Bool) {
        Task { @MainActor [weak self] in
            self?.detailVisible = visible
        }
    }

    private func sessionsChanged() {
        refreshHistory()
        refreshQuota(force: true)
    }

    private func refreshQuota(force: Bool) {
        guard !quotaRequestInFlight else { return }
        let now = Date()
        let elapsed = now.timeIntervalSince(lastQuotaRequest)
        if elapsed < 2 {
            if force {
                nextQuotaRefresh = min(nextQuotaRefresh, now.addingTimeInterval(2 - elapsed))
            }
            return
        }
        quotaRequestInFlight = true
        lastQuotaRequest = now
        let service = quotaService
        Task.detached(priority: .utility) { [weak self, service] in
            let latest = await service.fetch()
            await self?.applyQuota(latest)
        }
    }

    private func applyQuota(_ latest: QuotaSnapshot) {
        var display = latest
        if latest.available {
            lastValidQuota = latest
        } else if let valid = lastValidQuota,
                  Date().timeIntervalSince(valid.sampledAt) <= 30 * 60 {
            display.plan = valid.plan
            display.weeklyRemaining = valid.weeklyRemaining
            display.weeklyReset = valid.weeklyReset
        }
        quota = display
        quotaRequestInFlight = false
        let closeToReset = display.weeklyReset.map {
            $0.timeIntervalSinceNow > -60 && $0.timeIntervalSinceNow < 15 * 60
        } ?? false
        nextQuotaRefresh = Date().addingTimeInterval(closeToReset ? 10 : 30)
        orb.update(snapshot: display, preferences: preferences)
    }

    private func refreshHistory() {
        guard !historyRequestInFlight else { return }
        historyRequestInFlight = true
        let service = historyService
        Task.detached(priority: .utility) { [weak self, service] in
            let latest = service.read()
            await self?.applyHistory(latest)
        }
    }

    private func applyHistory(_ latest: TokenHistorySnapshot) {
        history = latest
        historyRequestInFlight = false
        details.update(snapshot: latest, language: preferences.language)
    }

    func orbPanelDidRequestToggle(_ controller: OrbPanelController) {
        let willExpand = !controller.isExpanded
        controller.setExpanded(willExpand)
        if willExpand { refreshQuota(force: true) }
        outsideSince = nil
    }

    func orbPanelDidRequestDetails(_ controller: OrbPanelController) {
        controller.setExpanded(false)
        outsideSince = nil
        details.update(snapshot: history, language: preferences.language)
        details.present()
        refreshHistory()
    }

    func orbPanelDidRequestLanguage(_ controller: OrbPanelController) {
        preferences.language = preferences.language == .chinese ? .english : .chinese
        store.save(preferences)
        orb.update(snapshot: quota, preferences: preferences)
        details.update(snapshot: history, language: preferences.language)
    }

    func orbPanelDidRequestPin(_ controller: OrbPanelController) {
        preferences.alwaysOnTop.toggle()
        store.save(preferences)
        orb.update(snapshot: quota, preferences: preferences)
        updateVisibility()
    }

    func orbPanelDidRequestQuit(_ controller: OrbPanelController) {
        NSApp.terminate(nil)
    }

    func orbPanel(_ controller: OrbPanelController, didMoveAnchor anchor: NSPoint) {
        preferences.hasCustomAnchor = true
        preferences.anchorX = anchor.x
        preferences.anchorY = anchor.y
        saveAnchorTask?.cancel()
        let pending = preferences
        saveAnchorTask = Task { @MainActor [weak self, pending] in
            try? await Task.sleep(nanoseconds: 250_000_000)
            guard !Task.isCancelled else { return }
            self?.store.save(pending)
        }
    }
}
