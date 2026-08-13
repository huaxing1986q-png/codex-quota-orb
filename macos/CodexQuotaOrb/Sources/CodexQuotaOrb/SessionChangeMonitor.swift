import Foundation

final class SessionChangeMonitor {
    private var timer: DispatchSourceTimer?
    private var lastSignature: String?
    private var pendingWork: DispatchWorkItem?
    private let callback: () -> Void
    private let queue = DispatchQueue(label: "CodexQuotaOrb.session-monitor", qos: .utility)

    init(callback: @escaping () -> Void) {
        self.callback = callback
    }

    func start() {
        guard timer == nil else { return }
        let source = DispatchSource.makeTimerSource(queue: queue)
        source.schedule(deadline: .now(), repeating: .milliseconds(500), leeway: .milliseconds(100))
        source.setEventHandler { [weak self] in self?.poll() }
        source.resume()
        timer = source
    }

    func stop() {
        pendingWork?.cancel()
        pendingWork = nil
        timer?.cancel()
        timer = nil
    }

    private func poll() {
        let signature = currentSignature()
        defer { lastSignature = signature }
        guard let lastSignature, signature != lastSignature else { return }
        pendingWork?.cancel()
        let work = DispatchWorkItem { [callback] in
            DispatchQueue.main.async(execute: callback)
        }
        pendingWork = work
        queue.asyncAfter(deadline: .now() + .milliseconds(250), execute: work)
    }

    private func currentSignature() -> String {
        let environment = ProcessInfo.processInfo.environment
        let home: URL
        if let custom = environment["CODEX_HOME"], !custom.isEmpty {
            home = URL(fileURLWithPath: NSString(string: custom).expandingTildeInPath)
        } else {
            home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex")
        }
        var count = 0
        var totalSize: UInt64 = 0
        var latest: TimeInterval = 0
        for folder in ["sessions", "archived_sessions"] {
            let root = home.appendingPathComponent(folder, isDirectory: true)
            let keys: [URLResourceKey] = [.fileSizeKey, .contentModificationDateKey, .isRegularFileKey]
            guard let enumerator = FileManager.default.enumerator(
                at: root,
                includingPropertiesForKeys: keys,
                options: [.skipsHiddenFiles, .skipsPackageDescendants]
            ) else { continue }
            for case let url as URL in enumerator where url.pathExtension.lowercased() == "jsonl" {
                guard let values = try? url.resourceValues(forKeys: Set(keys)), values.isRegularFile == true else {
                    continue
                }
                count += 1
                totalSize &+= UInt64(max(0, values.fileSize ?? 0))
                latest = max(latest, values.contentModificationDate?.timeIntervalSince1970 ?? 0)
            }
        }
        return "\(count):\(totalSize):\(latest)"
    }

    deinit {
        stop()
    }
}
