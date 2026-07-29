import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

/// The three permissions a recorder needs on this platform, and how to ask for them.
enum Permissions {
    static var hasAccessibility: Bool { AXIsProcessTrusted() }

    static var hasScreenRecording: Bool { CGPreflightScreenCaptureAccess() }

    /// Shows the system prompt. macOS only shows it once per app, so the settings pane is
    /// offered as the way back afterwards.
    static func askForAccessibility() {
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
    }

    static func askForScreenRecording() {
        _ = CGRequestScreenCaptureAccess()
    }

    static func openAccessibilitySettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    static func openScreenRecordingSettings() {
        open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
    }

    private static func open(_ address: String) {
        if let url = URL(string: address) {
            NSWorkspace.shared.open(url)
        }
    }

    /// A plain description of what is missing, or nil when everything is in place.
    static var missing: String? {
        var lacking: [String] = []
        if !hasAccessibility { lacking.append("Accessibility") }
        if !hasScreenRecording { lacking.append("Screen Recording") }
        if lacking.isEmpty { return nil }
        return lacking.joined(separator: " and ")
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
