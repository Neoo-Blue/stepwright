import AppKit
import ApplicationServices
import Foundation

/// Everything known about the control that was used.
struct ElementInfo {
    var name: String = ""
    var role: String = ""
    var controlType: String = ""
    var windowTitle: String = ""
    var appName: String = ""
    var processId: pid_t = 0
    var isPassword: Bool = false
    var bounds: CGRect = .zero
    var windowBounds: CGRect = .zero

    var hasName: Bool { !name.trimmingCharacters(in: .whitespaces).isEmpty }
}

/// Reads the accessibility tree to find out what was clicked.
///
/// Every call is time boxed. A frozen application must never stall the recorder, and asking
/// another process about its interface is a cross process call that can hang.
enum Inspector {
    private static let systemWide = AXUIElementCreateSystemWide()
    private static var appNames: [pid_t: String] = [:]
    private static let namesLock = NSLock()

    static func resetCache() {
        namesLock.lock()
        appNames.removeAll()
        namesLock.unlock()
    }

    static func resolve(at point: CGPoint, timeout: TimeInterval = 0.7) -> ElementInfo {
        var info = ElementInfo()

        let work = { () -> ElementInfo in
            var found = ElementInfo()
            var element: AXUIElement?

            guard AXUIElementCopyElementAtPosition(
                systemWide,
                Float(point.x),
                Float(point.y),
                &element) == .success, let element else { return found }

            fill(&found, from: element)
            return found
        }

        if let answer = runGuarded(timeout: timeout, work: work) {
            info = answer
        }

        if info.appName.isEmpty, info.processId > 0 {
            info.appName = friendlyName(info.processId)
        }

        return info
    }

    /// The control the keyboard is pointed at, which is what typing goes into.
    static func focused(timeout: TimeInterval = 0.5) -> ElementInfo? {
        runGuarded(timeout: timeout) { () -> ElementInfo? in
            var app: CFTypeRef?
            guard AXUIElementCopyAttributeValue(
                systemWide,
                kAXFocusedApplicationAttribute as CFString,
                &app) == .success else { return nil }

            let application = app as! AXUIElement
            var focused: CFTypeRef?
            guard AXUIElementCopyAttributeValue(
                application,
                kAXFocusedUIElementAttribute as CFString,
                &focused) == .success else { return nil }

            var info = ElementInfo()
            fill(&info, from: focused as! AXUIElement)
            return info
        } ?? nil
    }

    private static func fill(_ info: inout ElementInfo, from element: AXUIElement) {
        info.role = string(element, kAXRoleAttribute) ?? ""
        info.controlType = tidyRole(string(element, kAXRoleDescriptionAttribute) ?? info.role)
        info.isPassword = info.role == "AXSecureTextField"

        var pid: pid_t = 0
        if AXUIElementGetPid(element, &pid) == .success {
            info.processId = pid
            info.appName = friendlyName(pid)
        }

        // A control names itself in one of several ways, in rough order of usefulness.
        let candidates = [
            string(element, kAXTitleAttribute),
            string(element, kAXDescriptionAttribute),
            string(element, "AXLabel"),
            valueString(element),
            string(element, kAXHelpAttribute),
        ]

        for candidate in candidates {
            if let candidate, !candidate.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                info.name = clean(candidate)
                break
            }
        }

        info.bounds = frame(element)

        // The window gives the heading for the step and a framing to crop back to.
        if let window = copyElement(element, kAXWindowAttribute)
            ?? copyElement(element, kAXTopLevelUIElementAttribute) {
            info.windowTitle = clean(string(window, kAXTitleAttribute) ?? "")
            info.windowBounds = frame(window)
        }

        // A control that only repeats the window title says nothing worth reading out.
        if !info.name.isEmpty, looksLikeWindowTitle(info.name, info.windowTitle) {
            info.name = ""
        }

        if info.name.isEmpty {
            climbForName(&info, from: element)
        }
    }

    /// Unnamed controls borrow a name from the nearest named ancestor.
    private static func climbForName(_ info: inout ElementInfo, from element: AXUIElement) {
        var cursor: AXUIElement? = element

        for _ in 0..<4 {
            guard let parent = copyElement(cursor!, kAXParentAttribute) else { return }
            cursor = parent

            let name = clean(string(parent, kAXTitleAttribute)
                ?? string(parent, kAXDescriptionAttribute)
                ?? "")

            if name.isEmpty { continue }
            if looksLikeWindowTitle(name, info.windowTitle) { return }

            info.name = name
            if info.controlType.isEmpty {
                info.controlType = tidyRole(string(parent, kAXRoleDescriptionAttribute) ?? "")
            }

            return
        }
    }

    private static func looksLikeWindowTitle(_ name: String, _ windowTitle: String) -> Bool {
        let left = name.trimmingCharacters(in: .whitespaces)
        let right = windowTitle.trimmingCharacters(in: .whitespaces)

        if left.isEmpty || right.isEmpty { return false }
        if left.caseInsensitiveCompare(right) == .orderedSame { return true }

        return left.count >= 12
            && (right.lowercased().hasPrefix(left.lowercased())
                || left.lowercased().hasPrefix(right.lowercased()))
    }

    private static func frame(_ element: AXUIElement) -> CGRect {
        var position = CGPoint.zero
        var size = CGSize.zero

        var positionValue: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXPositionAttribute as CFString, &positionValue) == .success,
           let positionValue {
            AXValueGetValue(positionValue as! AXValue, .cgPoint, &position)
        }

        var sizeValue: CFTypeRef?
        if AXUIElementCopyAttributeValue(element, kAXSizeAttribute as CFString, &sizeValue) == .success,
           let sizeValue {
            AXValueGetValue(sizeValue as! AXValue, .cgSize, &size)
        }

        if size.width <= 0 || size.height <= 0 { return .zero }
        return CGRect(origin: position, size: size)
    }

    private static func copyElement(_ element: AXUIElement, _ attribute: String) -> AXUIElement? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success,
              let value else { return nil }

        if CFGetTypeID(value) == AXUIElementGetTypeID() {
            return (value as! AXUIElement)
        }

        return nil
    }

    private static func string(_ element: AXUIElement, _ attribute: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute as CFString, &value) == .success,
              let text = value as? String else { return nil }
        return text
    }

    /// The value of a control, but only when it is short enough to be a label rather than
    /// the contents of a document.
    private static func valueString(_ element: AXUIElement) -> String? {
        guard let value = string(element, kAXValueAttribute) else { return nil }
        return value.count <= 60 ? value : nil
    }

    private static func tidyRole(_ role: String) -> String {
        var text = role.trimmingCharacters(in: .whitespaces).lowercased()
        if text.hasPrefix("ax") { text = String(text.dropFirst(2)) }
        return text
    }

    private static func clean(_ value: String) -> String {
        var text = value.replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
            .trimmingCharacters(in: .whitespaces)

        while text.contains("  ") {
            text = text.replacingOccurrences(of: "  ", with: " ")
        }

        return text
    }

    static func friendlyName(_ pid: pid_t) -> String {
        namesLock.lock()
        if let known = appNames[pid] {
            namesLock.unlock()
            return known
        }
        namesLock.unlock()

        let name = NSRunningApplication(processIdentifier: pid)?.localizedName ?? ""

        if !name.isEmpty {
            namesLock.lock()
            appNames[pid] = name
            namesLock.unlock()
        }

        return name
    }

    /// Runs a lookup with a deadline, because another process can hang for any reason.
    private static func runGuarded<T>(timeout: TimeInterval, work: @escaping () -> T) -> T? {
        var answer: T?
        let done = DispatchSemaphore(value: 0)

        DispatchQueue.global(qos: .userInitiated).async {
            answer = work()
            done.signal()
        }

        return done.wait(timeout: .now() + timeout) == .success ? answer : nil
    }
}
