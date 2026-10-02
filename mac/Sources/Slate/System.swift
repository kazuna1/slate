import AppKit
import ServiceManagement
import UserNotifications

/// "Launch at login" through the system login-items list.
enum LoginItem {
    static var status: SMAppService.Status { SMAppService.mainApp.status }
    static var isEnabled: Bool { status == .enabled }

    static var statusText: String {
        switch status {
        case .enabled: return "on"
        case .requiresApproval: return "waiting for approval in System Settings → General → Login Items"
        case .notRegistered: return "off"
        case .notFound: return "not found"
        @unknown default: return "unknown"
        }
    }

    static func openSettings() { SMAppService.openSystemSettingsLoginItems() }

    static func set(_ enabled: Bool) throws {
        if enabled { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
    }
}

/// System notifications; one kind is clickable (install update).
/// macOS may refuse notifications for apps without a paid Apple signature; then `fallback`
/// (a message shown in the bar itself) is used instead.
final class Notifier: NSObject, UNUserNotificationCenterDelegate {
    var onUpdateClicked: (() -> Void)?
    var fallback: ((_ title: String, _ body: String, _ isUpdate: Bool) -> Void)?

    /// UserNotifications needs an app bundle; under `swift run` we only log.
    private let hasBundle = Bundle.main.bundleIdentifier != nil
    private var allowed = false

    override init() {
        super.init()
        guard hasBundle else { return }
        let center = UNUserNotificationCenter.current()
        center.delegate = self
        center.requestAuthorization(options: [.alert, .sound]) { granted, error in
            if let error { Log.error("Notification permission", error) }
            DispatchQueue.main.async { self.allowed = granted }
        }
    }

    func post(_ title: String, _ body: String, isUpdate: Bool = false) {
        Log.write("notify: \(title) — \(body)")
        guard hasBundle, allowed else {
            fallback?(title, body, isUpdate)
            return
        }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        if isUpdate { content.userInfo = ["action": "update"] }
        let request = UNNotificationRequest(identifier: isUpdate ? "update" : UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }

    // Show banners even while Slate counts as the active app.
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification,
                                withCompletionHandler completionHandler: @escaping (UNNotificationPresentationOptions) -> Void) {
        completionHandler([.banner, .list])
    }

    func userNotificationCenter(_ center: UNUserNotificationCenter, didReceive response: UNNotificationResponse,
                                withCompletionHandler completionHandler: @escaping () -> Void) {
        if response.notification.request.content.userInfo["action"] as? String == "update" {
            DispatchQueue.main.async { self.onUpdateClicked?() }
        }
        completionHandler()
    }
}
