import CoreGraphics
import Foundation

/// Turns an interaction into the sentence a reader will follow.
/// House style: plain imperative English, and no dashes anywhere.
enum StepText {
    private static let vagueTypes: Set<String> = [
        "group", "unknown", "window", "sheet", "layout area", "layout item", "splitter group",
        "scroll area", "generic", "section", "region", "", "application", "system dialog",
    ]

    static func describe(
        kind: StepKind,
        element: ElementInfo,
        place: String,
        extra: String = "") -> String {
        let target = describeTarget(element)
        let inApp = place.isEmpty ? "" : " in " + place

        switch kind {
        case .click:
            return "Click \(target)\(inApp)."
        case .doubleClick:
            return "Double click \(target)\(inApp)."
        case .rightClick:
            return "Right click \(target)\(inApp)."
        case .middleClick:
            return "Middle click \(target)\(inApp)."
        case .drag:
            return "Drag \(target) \(extra)\(inApp)."
        case .scroll:
            return "Scroll \(extra)\(inApp)."
        case .hotkey:
            return "Press \(extra)\(inApp)."
        case .type:
            return describeTyping(element: element, typed: extra, inApp: inApp)
        case .screenshot:
            return "Review this screen."
        case .heading:
            return place
        default:
            return "Continue."
        }
    }

    static func describeTyping(element: ElementInfo, typed: String, inApp: String) -> String {
        let field = fieldName(element)
        if typed.isEmpty {
            return "Enter your details\(field)\(inApp)."
        }

        return "Type \(quote(typed))\(field)\(inApp)."
    }

    static func describeRedactedTyping(element: ElementInfo, inApp: String) -> String {
        let field = fieldName(element)
        return field.isEmpty ? "Enter your password\(inApp)." : "Enter your password\(field)\(inApp)."
    }

    private static func fieldName(_ element: ElementInfo) -> String {
        guard element.hasName else { return "" }

        let name = shorten(element.name, 48)
        let type = vagueTypes.contains(element.controlType) ? "field" : element.controlType
        return " in the \(quote(name)) \(type)"
    }

    static func describeTarget(_ element: ElementInfo) -> String {
        let type = element.controlType
        let useful = !vagueTypes.contains(type)

        if element.hasName {
            let name = quote(shorten(element.name, 60))
            return useful ? "\(name) \(type)" : name
        }

        return useful ? "the \(type)" : "the highlighted spot"
    }

    static func scrollDirection(_ delta: Double, horizontal: Bool) -> String {
        if horizontal {
            return delta < 0 ? "right" : "left"
        }

        return delta < 0 ? "down" : "up"
    }

    static func appContext(_ element: ElementInfo) -> String {
        if !element.appName.isEmpty { return element.appName }
        if !element.windowTitle.isEmpty { return shorten(element.windowTitle, 40) }
        return ""
    }

    static func shorten(_ value: String, _ max: Int) -> String {
        let clean = value.trimmingCharacters(in: .whitespaces)
        if clean.count <= max { return clean }
        return String(clean.prefix(max)).trimmingCharacters(in: .whitespaces) + "..."
    }

    private static func quote(_ value: String) -> String { "\u{201C}" + value + "\u{201D}" }
}

/// Names for the keys that have no character of their own.
enum KeyNames {
    private static let named: [CGKeyCode: String] = [
        36: "Return", 48: "Tab", 49: "Space", 51: "Delete", 53: "Esc",
        76: "Enter", 71: "Clear", 114: "Help", 115: "Home", 116: "Page Up",
        117: "Forward Delete", 119: "End", 121: "Page Down",
        123: "Left arrow", 124: "Right arrow", 125: "Down arrow", 126: "Up arrow",
        122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6",
        98: "F7", 100: "F8", 101: "F9", 109: "F10", 103: "F11", 111: "F12",
    ]

    static func name(for code: CGKeyCode) -> String? { named[code] }

    static var notableCodes: Set<CGKeyCode> {
        Set(named.keys).subtracting([49])
    }

    /// The shortcut as a person would read it out.
    static func combination(
        command: Bool,
        option: Bool,
        control: Bool,
        shift: Bool,
        key: String) -> String {
        var parts: [String] = []
        if control { parts.append("Control") }
        if option { parts.append("Option") }
        if shift { parts.append("Shift") }
        if command { parts.append("Command") }
        parts.append(key)
        return parts.joined(separator: " + ")
    }
}
