import AppKit
import Foundation
import Security

/// Everything the person can change, kept in the usual place for preferences. The assistant
/// key is the one exception: it lives in the keychain.
final class Settings {
    private let store = UserDefaults.standard

    var author: String {
        get { string("author", NSFullUserName()) }
        set { store.set(newValue, forKey: "author") }
    }

    var countdownSeconds: Int {
        get { integer("countdownSeconds", 3) }
        set { store.set(newValue, forKey: "countdownSeconds") }
    }

    var captureAllDisplays: Bool {
        get { bool("captureAllDisplays", false) }
        set { store.set(newValue, forKey: "captureAllDisplays") }
    }

    var captureKeyboard: Bool {
        get { bool("captureKeyboard", true) }
        set { store.set(newValue, forKey: "captureKeyboard") }
    }

    var captureScroll: Bool {
        get { bool("captureScroll", true) }
        set { store.set(newValue, forKey: "captureScroll") }
    }

    var captureDrag: Bool {
        get { bool("captureDrag", true) }
        set { store.set(newValue, forKey: "captureDrag") }
    }

    var typingMergeSeconds: Double {
        get { double("typingMergeSeconds", 1.4) }
        set { store.set(newValue, forKey: "typingMergeSeconds") }
    }

    var redactPasswords: Bool {
        get { bool("redactPasswords", true) }
        set { store.set(newValue, forKey: "redactPasswords") }
    }

    var redactPatterns: [String] {
        get { store.stringArray(forKey: "redactPatterns") ?? [] }
        set { store.set(newValue, forKey: "redactPatterns") }
    }

    var autoZoom: Bool {
        get { bool("autoZoom", true) }
        set { store.set(newValue, forKey: "autoZoom") }
    }

    var zoomPadding: Int {
        get { integer("zoomPadding", 260) }
        set { store.set(newValue, forKey: "zoomPadding") }
    }

    var showClickMarker: Bool {
        get { bool("showClickMarker", true) }
        set { store.set(newValue, forKey: "showClickMarker") }
    }

    var showElementOutline: Bool {
        get { bool("showElementOutline", true) }
        set { store.set(newValue, forKey: "showElementOutline") }
    }

    var markerColor: String {
        get { string("markerColor", "FF3B30") }
        set { store.set(newValue, forKey: "markerColor") }
    }

    var gifMotion: String {
        get { string("gifMotion", "Normal") }
        set { store.set(newValue, forKey: "gifMotion") }
    }

    var gifWidth: Int {
        get { integer("gifWidth", 760) }
        set { store.set(newValue, forKey: "gifWidth") }
    }

    var aiEnabled: Bool {
        get { bool("aiEnabled", false) }
        set { store.set(newValue, forKey: "aiEnabled") }
    }

    var aiProvider: String {
        get { string("aiProvider", "openai") }
        set { store.set(newValue, forKey: "aiProvider") }
    }

    var aiBaseUrl: String {
        get { string("aiBaseUrl", "https://api.openai.com/v1") }
        set { store.set(newValue, forKey: "aiBaseUrl") }
    }

    var aiModel: String {
        get { string("aiModel", "gpt-4o-mini") }
        set { store.set(newValue, forKey: "aiModel") }
    }

    var aiSendScreenshots: Bool {
        get { bool("aiSendScreenshots", false) }
        set { store.set(newValue, forKey: "aiSendScreenshots") }
    }

    var aiWriteNotes: Bool {
        get { bool("aiWriteNotes", true) }
        set { store.set(newValue, forKey: "aiWriteNotes") }
    }

    var libraryFolder: URL {
        get {
            if let path = store.string(forKey: "libraryFolder") {
                return URL(fileURLWithPath: path)
            }

            return FileManager.default
                .urls(for: .documentDirectory, in: .userDomainMask)[0]
                .appendingPathComponent("Stepwright")
        }
        set { store.set(newValue.path, forKey: "libraryFolder") }
    }

    // ------------------------------------------------------------------ the assistant key

    private let keyAccount = "assistant"
    private let keyService = "com.stepwright.app"

    var hasAiKey: Bool { !aiKey.isEmpty }

    var aiKey: String {
        get {
            let query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: keyService,
                kSecAttrAccount as String: keyAccount,
                kSecReturnData as String: true,
                kSecMatchLimit as String: kSecMatchLimitOne,
            ]

            var item: CFTypeRef?
            guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
                  let data = item as? Data,
                  let text = String(data: data, encoding: .utf8) else { return "" }

            return text
        }
        set {
            let query: [String: Any] = [
                kSecClass as String: kSecClassGenericPassword,
                kSecAttrService as String: keyService,
                kSecAttrAccount as String: keyAccount,
            ]

            SecItemDelete(query as CFDictionary)

            guard !newValue.isEmpty, let data = newValue.data(using: .utf8) else { return }

            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlocked
            SecItemAdd(add as CFDictionary, nil)
        }
    }

    // ------------------------------------------------------------------ helpers

    /// Replaces anything matching a pattern the person asked to hide.
    func redact(_ text: String) -> String {
        var result = text

        for pattern in redactPatterns where !pattern.isEmpty {
            guard let expression = try? NSRegularExpression(
                pattern: pattern,
                options: [.caseInsensitive]) else { continue }

            result = expression.stringByReplacingMatches(
                in: result,
                options: [],
                range: NSRange(result.startIndex..., in: result),
                withTemplate: "hidden")
        }

        return result
    }

    private func string(_ key: String, _ fallback: String) -> String {
        store.string(forKey: key) ?? fallback
    }

    private func bool(_ key: String, _ fallback: Bool) -> Bool {
        store.object(forKey: key) == nil ? fallback : store.bool(forKey: key)
    }

    private func integer(_ key: String, _ fallback: Int) -> Int {
        store.object(forKey: key) == nil ? fallback : store.integer(forKey: key)
    }

    private func double(_ key: String, _ fallback: Double) -> Double {
        store.object(forKey: key) == nil ? fallback : store.double(forKey: key)
    }
}

extension NSColor {
    /// Reads a colour written as six hex characters.
    static func fromHex(_ hex: String, fallback: NSColor = .systemRed) -> NSColor {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text = String(text.dropFirst()) }
        guard text.count == 6, let value = Int(text, radix: 16) else { return fallback }

        return NSColor(
            srgbRed: CGFloat((value >> 16) & 0xFF) / 255,
            green: CGFloat((value >> 8) & 0xFF) / 255,
            blue: CGFloat(value & 0xFF) / 255,
            alpha: 1)
    }

    var hexString: String {
        guard let rgb = usingColorSpace(.sRGB) else { return "FF3B30" }
        return String(
            format: "%02X%02X%02X",
            Int((rgb.redComponent * 255).rounded()),
            Int((rgb.greenComponent * 255).rounded()),
            Int((rgb.blueComponent * 255).rounded()))
    }
}
