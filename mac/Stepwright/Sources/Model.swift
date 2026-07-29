import Foundation

/// The guide document. The shape on disk is identical to the Windows build, so a
/// `.stepwright` file written on either platform opens on the other.
enum StepKind: String, Codable {
    case click = "Click"
    case doubleClick = "DoubleClick"
    case rightClick = "RightClick"
    case middleClick = "MiddleClick"
    case drag = "Drag"
    case type = "Type"
    case hotkey = "Hotkey"
    case scroll = "Scroll"
    case screenshot = "Screenshot"
    case note = "Note"
    case heading = "Heading"
}

enum AnnotationKind: String, Codable {
    case rectangle = "Rectangle"
    case arrow = "Arrow"
    case blur = "Blur"
    case highlight = "Highlight"
    case text = "Text"
    case number = "Number"
}

struct RectI: Codable, Equatable {
    var X: Int
    var Y: Int
    var W: Int
    var H: Int

    init(_ x: Int, _ y: Int, _ w: Int, _ h: Int) {
        X = x
        Y = y
        W = w
        H = h
    }

    init(_ rect: CGRect) {
        X = Int(rect.origin.x.rounded())
        Y = Int(rect.origin.y.rounded())
        W = Int(rect.size.width.rounded())
        H = Int(rect.size.height.rounded())
    }

    var rect: CGRect { CGRect(x: X, y: Y, width: W, height: H) }
    var isEmpty: Bool { W <= 0 || H <= 0 }
}

struct PointI: Codable, Equatable {
    var X: Int
    var Y: Int

    init(_ x: Int, _ y: Int) {
        X = x
        Y = y
    }

    init(_ point: CGPoint) {
        X = Int(point.x.rounded())
        Y = Int(point.y.rounded())
    }

    var point: CGPoint { CGPoint(x: X, y: Y) }
}

final class Annotation: Codable {
    var Kind: AnnotationKind = .rectangle
    var Area: RectI = RectI(0, 0, 0, 0)
    var Color: String = "FF3B30"
    var Text: String = ""
    var Thickness: Int = 4
    var Number: Int = 0
}

final class Step: Codable {
    var Id: String = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    var Kind: StepKind = .click
    var Text: String = ""
    var OriginalText: String = ""
    var Notes: String = ""
    var Moment: Date = Date()
    var Image: String = ""
    var ClickPoint: PointI?
    var ElementArea: RectI?
    var WindowArea: RectI?
    var Crop: RectI?
    var Animate: Bool = false
    var ShowClickMarker: Bool = true
    var ShowElementOutline: Bool = true
    var AutoZoom: Bool = true
    var Skip: Bool = false
    var Annotations: [Annotation] = []
    var AppName: String = ""
    var WindowTitle: String = ""
    var ElementName: String = ""
    var ElementType: String = ""
    var TypedText: String = ""
    var Keys: String = ""
    var Redacted: Bool = false

    var hasImage: Bool { !Image.isEmpty }

    init() {}

    func copy() -> Step {
        let clone = Step()
        clone.Kind = Kind
        clone.Text = Text
        clone.OriginalText = OriginalText
        clone.Notes = Notes
        clone.Moment = Moment
        clone.Image = Image
        clone.ClickPoint = ClickPoint
        clone.ElementArea = ElementArea
        clone.WindowArea = WindowArea
        clone.Crop = Crop
        clone.Animate = Animate
        clone.ShowClickMarker = ShowClickMarker
        clone.ShowElementOutline = ShowElementOutline
        clone.AutoZoom = AutoZoom
        clone.Skip = Skip
        clone.AppName = AppName
        clone.WindowTitle = WindowTitle
        clone.ElementName = ElementName
        clone.ElementType = ElementType
        clone.TypedText = TypedText
        clone.Keys = Keys
        clone.Redacted = Redacted
        return clone
    }
}

final class Guide: Codable {
    var FormatVersion: String = "1"
    var Title: String = "Untitled guide"
    var Summary: String = ""
    var Author: String = ""
    var Created: Date = Date()
    var Updated: Date = Date()
    var Steps: [Step] = []

    /// Where the screenshots live while the guide is open. Never written to the file.
    var mediaFolder: URL?
    var filePath: URL?
    var dirty: Bool = false

    enum CodingKeys: String, CodingKey {
        case FormatVersion, Title, Summary, Author, Created, Updated, Steps
    }

    init() {}

    var visible: [Step] { Steps.filter { !$0.Skip } }

    func imagePath(_ step: Step) -> URL? {
        guard step.hasImage, let folder = mediaFolder else { return nil }
        return folder.appendingPathComponent(step.Image)
    }

    static func decoder() -> JSONDecoder {
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return decoder
    }

    static func encoder() -> JSONEncoder {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}
