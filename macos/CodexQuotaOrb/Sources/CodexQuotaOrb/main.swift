import AppKit
import Darwin
import Foundation

if CommandLine.arguments.contains("--self-test") {
    let failures = QuotaService().fixtureSelfTest() + TokenHistoryService().fixtureSelfTest()
    let output: [String: Any] = [
        "passed": failures.isEmpty,
        "tests": 7,
        "failures": failures
    ]
    let data = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
    print(String(decoding: data, as: UTF8.self))
    exit(failures.isEmpty ? 0 : 1)
}

MainActor.assumeIsolated {
    let application = NSApplication.shared
    let applicationDelegate = AppDelegate()
    application.delegate = applicationDelegate
    application.run()
}
