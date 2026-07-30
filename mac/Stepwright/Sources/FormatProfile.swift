import Foundation

/// How a guide is turned into markup. Every system that receives a document wants something
/// slightly different, so the rules live in a file that can be edited, shared and swapped
/// rather than being buried in the exporter.
///
/// The shape matches the Windows build exactly, so a format file works on either.
struct FormatProfile: Codable {
    var Name: String = "Untitled format"
    var Description: String = ""

    // Shape
    var InlineStyles: Bool = false
    var SingleContainer: Bool = false
    var UseOrderedList: Bool = true
    var UseHeadingTags: Bool = true
    var AllowColor: Bool = true

    var IncludeTitle: Bool = true
    var IncludeSummary: Bool = true
    var IncludeMeta: Bool = true
    var IncludeFooter: Bool = true

    // Type
    var FontFamily: String = "-apple-system, system-ui, Helvetica, Arial, sans-serif"
    var TitleSize: Int = 32
    var HeadingSize: Int = 20
    var BodySize: Int = 17
    var NoteSize: Int = 15
    var MetaSize: Int = 13
    var BlockSpacing: Int = 12

    // Steps
    var StepPrefix: String = ""
    var StepSuffix: String = "."
    var BoldStepText: Bool = false
    var NotePrefix: String = ""

    // Pictures
    var ImageWidth: Int = 1400
    var UseJpeg: Bool = false
    var JpegQuality: Double = 82
    var AllowAnimation: Bool = true
    var EmbedImages: Bool = true
    var ImageDisplayWidth: Int = 0
    var RoundImageCorners: Bool = true

    /// Written in place of each picture. The number of the step replaces {n}.
    var ImagePlaceholder: String = ""

    // Extras
    var FooterText: String = "Made with Stepwright"
    var Preamble: String = ""

    var isBuiltIn: Bool = false

    enum CodingKeys: String, CodingKey {
        case Name, Description, InlineStyles, SingleContainer, UseOrderedList, UseHeadingTags
        case AllowColor, IncludeTitle, IncludeSummary, IncludeMeta, IncludeFooter
        case FontFamily, TitleSize, HeadingSize, BodySize, NoteSize, MetaSize, BlockSpacing
        case StepPrefix, StepSuffix, BoldStepText, NotePrefix
        case ImageWidth, UseJpeg, JpegQuality, AllowAnimation, EmbedImages
        case ImageDisplayWidth, RoundImageCorners, ImagePlaceholder
        case FooterText, Preamble
    }
}

/// The formats that ship with the app, and the folder holding any others.
enum FormatProfiles {
    static let fileExtension = "swformat"

    static var folder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Stepwright/formats")
    }

    /// The look the app uses unless told otherwise.
    static func standard() -> FormatProfile {
        var profile = FormatProfile()
        profile.Name = "Stepwright"
        profile.Description = "The look the app uses by default: a styled page with rounded pictures."
        profile.isBuiltIn = true
        profile.SingleContainer = true
        profile.BoldStepText = true
        return profile
    }

    /// What Hudu accepts: one container, inline styles, Arial, sixteen point bold headings,
    /// fourteen point body, no tables and no colour so the site controls light and dark mode.
    static func hudu() -> FormatProfile {
        var profile = FormatProfile()
        profile.Name = "Hudu"
        profile.Description = "One container, inline styles, Arial, no colour so Hudu controls light and dark mode."
        profile.isBuiltIn = true
        profile.InlineStyles = true
        profile.SingleContainer = true
        profile.UseHeadingTags = false
        profile.AllowColor = false
        profile.IncludeMeta = false
        profile.FontFamily = "Arial, sans-serif"
        profile.TitleSize = 16
        profile.HeadingSize = 16
        profile.BodySize = 14
        profile.NoteSize = 14
        profile.MetaSize = 12
        profile.BoldStepText = false
        profile.NotePrefix = "Note: "
        profile.UseJpeg = true
        profile.JpegQuality = 78
        profile.ImageWidth = 1100
        profile.ImageDisplayWidth = 700
        profile.RoundImageCorners = false
        profile.AllowAnimation = false
        profile.FooterText = "Published from Stepwright on {date}"
        return profile
    }

    /// Confluence keeps pictures as attachments and refers to them by name, so the markup
    /// carries a reference rather than the picture itself.
    static func confluence() -> FormatProfile {
        var profile = FormatProfile()
        profile.Name = "Confluence"
        profile.Description = "Storage format, with pictures attached to the page and referred to by name."
        profile.isBuiltIn = true
        profile.UseOrderedList = false
        profile.AllowColor = false
        profile.FontFamily = ""
        profile.BoldStepText = true
        profile.UseJpeg = true
        profile.JpegQuality = 80
        profile.ImageWidth = 1100
        profile.ImageDisplayWidth = 700
        profile.AllowAnimation = false
        profile.RoundImageCorners = false
        profile.ImagePlaceholder = "<ac:image ac:width=\"700\"><ri:attachment ri:filename=\"step{n}.jpg\" /></ac:image>"
        profile.FooterText = "Published from Stepwright on {date}"
        return profile
    }

    /// Markup with nothing added, for pasting into an editor that styles it itself.
    static func plain() -> FormatProfile {
        var profile = FormatProfile()
        profile.Name = "Plain"
        profile.Description = "Headings, paragraphs and pictures with no styling at all."
        profile.isBuiltIn = true
        profile.AllowColor = false
        profile.IncludeMeta = false
        profile.IncludeFooter = false
        profile.FontFamily = ""
        profile.BoldStepText = true
        profile.RoundImageCorners = false
        return profile
    }

    static func builtIn() -> [FormatProfile] { [standard(), hudu(), confluence(), plain()] }

    /// Everything that ships with the app plus everything saved beside it.
    static func all() -> [FormatProfile] {
        var profiles = builtIn()

        guard let files = try? FileManager.default.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: nil) else { return profiles }

        for file in files where file.pathExtension == fileExtension {
            guard let loaded = load(file) else { continue }

            // A saved format with the same name replaces the one that ships with the app.
            profiles.removeAll { $0.Name.caseInsensitiveCompare(loaded.Name) == .orderedSame }
            profiles.append(loaded)
        }

        return profiles
    }

    static func find(_ name: String?) -> FormatProfile {
        let profiles = all()
        guard let name else { return profiles[0] }
        return profiles.first { $0.Name.caseInsensitiveCompare(name) == .orderedSame } ?? profiles[0]
    }

    static func load(_ url: URL) -> FormatProfile? {
        guard let data = try? Data(contentsOf: url),
              var profile = try? JSONDecoder().decode(FormatProfile.self, from: data),
              !profile.Name.isEmpty else { return nil }

        profile.isBuiltIn = false
        return profile
    }

    static func save(_ profile: FormatProfile) {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try? write(profile, to: folder.appendingPathComponent(safeName(profile.Name) + "." + fileExtension))
    }

    static func write(_ profile: FormatProfile, to url: URL) throws {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(profile).write(to: url)
    }

    static func delete(_ name: String) {
        let url = folder.appendingPathComponent(safeName(name) + "." + fileExtension)
        try? FileManager.default.removeItem(at: url)
    }

    private static func safeName(_ name: String) -> String {
        let clean = name
            .replacingOccurrences(of: "/", with: " ")
            .replacingOccurrences(of: ":", with: " ")
            .trimmingCharacters(in: .whitespaces)

        return clean.isEmpty ? "format" : clean
    }
}
