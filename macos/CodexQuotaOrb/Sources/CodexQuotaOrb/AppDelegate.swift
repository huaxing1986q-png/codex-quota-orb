import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var monitor: MonitorController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        installApplicationMenu()
        monitor = MonitorController()
        monitor?.start()
    }

    func applicationWillTerminate(_ notification: Notification) {
        monitor?.stop()
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        false
    }

    private func installApplicationMenu() {
        let mainMenu = NSMenu()
        let appItem = NSMenuItem()
        let appMenu = NSMenu()
        let quit = NSMenuItem(
            title: "Quit Codex Quota Orb",
            action: #selector(NSApplication.terminate(_:)),
            keyEquivalent: "q"
        )
        appMenu.addItem(quit)
        appItem.submenu = appMenu
        mainMenu.addItem(appItem)
        NSApp.mainMenu = mainMenu
    }
}
