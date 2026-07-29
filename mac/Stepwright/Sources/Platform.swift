import AppKit
import ApplicationServices
import IOKit.hid
import CoreGraphics
import Foundation

/// What macOS has to allow before any of this can work, and how to ask for it.
///
/// Two things make this harder than it looks. An app run straight from a download is given a
/// random read only path on every launch, so a permission granted to it is attached to a path
/// that will not exist next time. And both of these permissions are read once when a process
/// starts, so granting one while the app is open does nothing until it is opened again.
enum Permissions {
    enum Kind: CaseIterable {
        case accessibility
        case inputMonitoring
        case screenRecording

        var title: String {
            switch self {
            case .accessibility: return "Accessibility"
            case .inputMonitoring: return "Input Monitoring"
            case .screenRecording: return "Screen Recording"
            }
        }

        var reason: String {
            switch self {
            case .accessibility:
                return "Reads the name of the control you click, so a step can say what you actually pressed."
            case .inputMonitoring:
                return "Sees your clicks and keystrokes, which is what the steps are made from."
            case .screenRecording:
                return "Takes the screenshot at the moment of each action."
            }
        }

        var settingsPane: String {
            switch self {
            case .accessibility:
                return "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"
            case .inputMonitoring:
                return "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent"
            case .screenRecording:
                return "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture"
            }
        }

        /// True when granting this one only takes effect after the app is opened again.
        var needsReopen: Bool { self != .accessibility }
    }

    static func granted(_ kind: Kind) -> Bool {
        switch kind {
        case .accessibility:
            return AXIsProcessTrusted()
        case .inputMonitoring:
            return IOHIDCheckAccess(kIOHIDRequestTypeListenEvent) == kIOHIDAccessTypeGranted
        case .screenRecording:
            return CGPreflightScreenCaptureAccess() || canSeeWindowNames()
        }
    }

    static var allGranted: Bool { Kind.allCases.allSatisfy { granted($0) } }

    static var missing: [Kind] { Kind.allCases.filter { !granted($0) } }

    /// Shows the system prompt. macOS only offers it once, so the settings pane is opened as
    /// the way back afterwards.
    static func request(_ kind: Kind) {
        switch kind {
        case .accessibility:
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(options)
        case .inputMonitoring:
            _ = IOHIDRequestAccess(kIOHIDRequestTypeListenEvent)
        case .screenRecording:
            _ = CGRequestScreenCaptureAccess()
        }

        openSettings(kind)
    }

    static func openSettings(_ kind: Kind) {
        if let url = URL(string: kind.settingsPane) {
            NSWorkspace.shared.open(url)
        }
    }

    /// Without this permission macOS hides the names of other applications' windows, which is
    /// a reliable way to tell whether it has really been granted.
    private static func canSeeWindowNames() -> Bool {
        guard let list = CGWindowListCopyWindowInfo(
            [.optionOnScreenOnly, .excludeDesktopElements],
            kCGNullWindowID) as? [[String: Any]] else { return false }

        let ours = ProcessInfo.processInfo.processIdentifier

        for window in list {
            guard let pid = window[kCGWindowOwnerPID as String] as? pid_t, pid != ours else { continue }
            if let name = window[kCGWindowName as String] as? String, !name.isEmpty { return true }
        }

        return false
    }

    // ------------------------------------------------------------------ where the app lives

    /// macOS runs an app straight from a download at a throwaway path that changes every
    /// time, which quietly breaks every permission granted to it.
    static var isRelocated: Bool {
        let path = Bundle.main.bundlePath
        return path.contains("/AppTranslocation/") || path.hasPrefix("/private/var/folders/")
    }

    static var isInApplications: Bool {
        let path = Bundle.main.bundlePath
        return path.hasPrefix("/Applications/") || path.hasPrefix(NSHomeDirectory() + "/Applications/")
    }

    /// True when permissions cannot be made to stick where the app currently is.
    static var needsMoving: Bool { isRelocated || !isInApplications }

    /// Moves the app into Applications and opens it again from there.
    @discardableResult
    static func moveToApplications() -> String? {
        let source = Bundle.main.bundleURL

        if isRelocated {
            return "macOS is running this copy from a temporary place, so it cannot move itself. "
                + "Drag Stepwright to your Applications folder, then open it from there."
        }

        let destination = URL(fileURLWithPath: "/Applications")
            .appendingPathComponent(source.lastPathComponent)

        do {
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }

            try FileManager.default.moveItem(at: source, to: destination)
            relaunch(at: destination)
            return nil
        } catch {
            return "The app could not move itself. Drag Stepwright to your Applications folder, "
                + "then open it from there."
        }
    }

    /// Opens a fresh copy and lets this one go, which is the only way a permission read at
    /// startup can be picked up.
    static func relaunch(at url: URL? = nil) {
        let target = url ?? Bundle.main.bundleURL

        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        task.arguments = ["-n", target.path]

        try? task.run()

        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
            NSApp.terminate(nil)
        }
    }
}

/// A screen grab, and where its top left corner sits on the desktop.
final class CapturedFrame {
    let image: CGImage
    let origin: CGPoint

    private var savedName: String?
    private let lock = NSLock()

    init(image: CGImage, origin: CGPoint) {
        self.image = image
        self.origin = origin
    }

    func toImagePoint(_ screenPoint: CGPoint) -> CGPoint {
        CGPoint(x: screenPoint.x - origin.x, y: screenPoint.y - origin.y)
    }

    func toImageRect(_ screenRect: CGRect) -> CGRect {
        CGRect(
            x: screenRect.origin.x - origin.x,
            y: screenRect.origin.y - origin.y,
            width: screenRect.width,
            height: screenRect.height)
    }

    /// Writes the picture once however many steps share this grab.
    func saveOnce(in folder: URL, name: @autoclosure () -> String) -> String {
        lock.lock()
        defer { lock.unlock() }

        if let savedName { return savedName }

        let chosen = name()
        let url = folder.appendingPathComponent(chosen)

        guard ImageFile.writePng(image, to: url) else {
            savedName = ""
            return ""
        }

        savedName = chosen
        return chosen
    }
}

enum ScreenCapture {
    /// Grabs the display holding a point, or the whole desktop when asked.
    ///
    /// The coordinates here are the ones the event system uses: the origin is the top left
    /// of the main display, with y growing downwards, which is not the same as the one
    /// AppKit windows use.
    static func grab(at point: CGPoint, allDisplays: Bool) -> CapturedFrame? {
        if allDisplays {
            let bounds = desktopBounds()
            guard let image = CGWindowListCreateImage(
                bounds,
                .optionOnScreenOnly,
                kCGNullWindowID,
                [.bestResolution, .shouldBeOpaque]) else { return nil }
            return CapturedFrame(image: image, origin: bounds.origin)
        }

        let display = displayContaining(point)
        let bounds = CGDisplayBounds(display)

        guard let image = CGWindowListCreateImage(
            bounds,
            .optionOnScreenOnly,
            kCGNullWindowID,
            [.bestResolution, .shouldBeOpaque]) else { return nil }

        return CapturedFrame(image: image, origin: bounds.origin)
    }

    static func displayContaining(_ point: CGPoint) -> CGDirectDisplayID {
        var display = CGDirectDisplayID()
        var count: UInt32 = 0

        if CGGetDisplaysWithPoint(point, 1, &display, &count) == .success, count > 0 {
            return display
        }

        return CGMainDisplayID()
    }

    static func desktopBounds() -> CGRect {
        var bounds = CGRect.zero
        var displays = [CGDirectDisplayID](repeating: 0, count: 16)
        var count: UInt32 = 0

        guard CGGetActiveDisplayList(16, &displays, &count) == .success else {
            return CGDisplayBounds(CGMainDisplayID())
        }

        for index in 0..<Int(count) {
            let area = CGDisplayBounds(displays[index])
            bounds = bounds.isEmpty ? area : bounds.union(area)
        }

        return bounds.isEmpty ? CGDisplayBounds(CGMainDisplayID()) : bounds
    }

    /// Where the pointer is, in the same coordinates as everything else here.
    static func cursorPosition() -> CGPoint {
        CGEvent(source: nil)?.location ?? .zero
    }
}

enum ImageFile {
    static func writePng(_ image: CGImage, to url: URL) -> Bool {
        guard let data = pngData(image) else { return false }
        do {
            try data.write(to: url)
            return true
        } catch {
            return false
        }
    }

    static func pngData(_ image: CGImage) -> Data? {
        encode(image, type: "public.png", properties: nil)
    }

    static func jpegData(_ image: CGImage, quality: Double) -> Data? {
        encode(
            image,
            type: "public.jpeg",
            properties: [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
    }

    private static func encode(_ image: CGImage, type: String, properties: CFDictionary?) -> Data? {
        let data = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            data as CFMutableData,
            type as CFString,
            1,
            nil) else { return nil }

        CGImageDestinationAddImage(destination, image, properties)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return data as Data
    }

    static func load(_ url: URL) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil) else { return nil }
        return CGImageSourceCreateImageAtIndex(source, 0, nil)
    }
}
