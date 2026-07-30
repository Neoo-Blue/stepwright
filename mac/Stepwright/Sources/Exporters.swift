import AppKit
import CoreGraphics
import Foundation

/// A guide on disk is a single file: a zip holding guide.json plus the screenshots, exactly
/// the same shape the Windows build writes, so a file opens on either platform.
enum GuideStore {
    static let fileExtension = "stepwright"

    static var workRoot: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appendingPathComponent("Stepwright/working")
    }

    static func createWorkFolder() -> URL {
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyyMMdd_HHmmss_SSS"

        let folder = workRoot.appendingPathComponent(stamp.string(from: Date()))
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    static func save(_ guide: Guide, to url: URL) throws {
        guide.Updated = Date()

        guard let media = guide.mediaFolder else {
            throw Failure.message("This guide has nowhere to read its screenshots from.")
        }

        // Everything is staged in a folder first, then zipped in one go.
        let staging = createWorkFolder().appendingPathComponent("stage")
        let stagedMedia = staging.appendingPathComponent("media")

        try FileManager.default.createDirectory(at: stagedMedia, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: staging.deletingLastPathComponent()) }

        let json = try Guide.encoder().encode(guide)
        try json.write(to: staging.appendingPathComponent("guide.json"))

        var written = Set<String>()

        for step in guide.Steps where step.hasImage {
            if written.contains(step.Image) { continue }
            written.insert(step.Image)

            let source = media.appendingPathComponent(step.Image)
            guard FileManager.default.fileExists(atPath: source.path) else { continue }
            try? FileManager.default.copyItem(to: stagedMedia.appendingPathComponent(step.Image), from: source)
        }

        let temporary = url.deletingLastPathComponent()
            .appendingPathComponent(url.lastPathComponent + ".writing")

        try? FileManager.default.removeItem(at: temporary)
        try Shell.run("/usr/bin/zip", ["-q", "-r", "-X", temporary.path, "guide.json", "media"], in: staging)

        // The old file is replaced in one step, so a failure cannot leave nothing behind.
        if FileManager.default.fileExists(atPath: url.path) {
            _ = try FileManager.default.replaceItemAt(url, withItemAt: temporary)
        } else {
            try FileManager.default.moveItem(at: temporary, to: url)
        }

        guide.filePath = url
        guide.dirty = false
    }

    static func load(_ url: URL) throws -> Guide {
        let folder = createWorkFolder()
        try Shell.run("/usr/bin/unzip", ["-qq", "-o", url.path, "-d", folder.path], in: nil)

        let jsonUrl = folder.appendingPathComponent("guide.json")
        guard FileManager.default.fileExists(atPath: jsonUrl.path) else {
            throw Failure.message("That file does not contain a guide.")
        }

        let guide = try Guide.decoder().decode(Guide.self, from: try Data(contentsOf: jsonUrl))
        guide.mediaFolder = folder.appendingPathComponent("media")
        guide.filePath = url
        guide.dirty = false
        return guide
    }

    static func suggestFileName(_ guide: Guide) -> String {
        var name = guide.Title.isEmpty ? "guide" : guide.Title
        name = name.replacingOccurrences(of: "/", with: " ")
            .replacingOccurrences(of: ":", with: " ")
            .trimmingCharacters(in: .whitespaces)

        if name.count > 60 { name = String(name.prefix(60)) }
        return name.isEmpty ? "guide" : name
    }

    static func cleanupOldWorkFolders(olderThan days: Int = 7) {
        let cutoff = Date().addingTimeInterval(-Double(days) * 86400)

        guard let folders = try? FileManager.default.contentsOfDirectory(
            at: workRoot,
            includingPropertiesForKeys: [.contentModificationDateKey]) else { return }

        for folder in folders {
            let values = try? folder.resourceValues(forKeys: [.contentModificationDateKey])
            if let modified = values?.contentModificationDate, modified < cutoff {
                try? FileManager.default.removeItem(at: folder)
            }
        }
    }
}

enum Failure: LocalizedError {
    case message(String)

    var errorDescription: String? {
        switch self {
        case let .message(text): return text
        }
    }
}

enum Shell {
    static func run(_ tool: String, _ arguments: [String], in folder: URL?) throws {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: tool)
        process.arguments = arguments
        if let folder { process.currentDirectoryURL = folder }

        let error = Pipe()
        process.standardError = error
        process.standardOutput = Pipe()

        try process.run()
        process.waitUntilExit()

        if process.terminationStatus != 0 {
            let text = String(
                data: error.fileHandleForReading.readDataToEndOfFile(),
                encoding: .utf8) ?? ""
            throw Failure.message("\(tool) failed. \(text)")
        }
    }
}

extension FileManager {
    func copyItem(to destination: URL, from source: URL) throws {
        if fileExists(atPath: destination.path) {
            try removeItem(at: destination)
        }

        try copyItem(at: source, to: destination)
    }
}

// ---------------------------------------------------------------------- web page

struct HtmlOptions {
    /// A fragment drops the page shell so the markup can be pasted into another system.
    var fragment = false

    /// The rules to write by. Without one the standard look is used.
    var format: FormatProfile?

    /// Overrides the format when set, for an export that writes pictures beside the file.
    var embedImages: Bool?

    var imageFolder: URL?
    var imageFolderName = "images"

    /// Collect the pictures rather than writing them into the markup.
    var collectImagesOnly = false
}

/// Written out as the document is built: the picture for each step, by step number. Used by a
/// system that wants the pictures attached separately rather than carried inline.
final class CollectedImages {
    private(set) var pictures: [Int: Data] = [:]

    func add(_ number: Int, _ data: Data) { pictures[number] = data }
}

enum HtmlExporter {
    static func build(
        guide: Guide,
        settings: Settings,
        options: HtmlOptions,
        collected: CollectedImages? = nil) -> String {
        let format = options.format ?? FormatProfiles.standard()
        var body = ""
        var number = 0

        if !format.Preamble.isEmpty {
            body += format.Preamble + "\n"
        }

        if format.SingleContainer {
            body += "<div\(style(format, container(format)))>\n"
        }

        if format.IncludeTitle, !guide.Title.isEmpty {
            body += format.UseHeadingTags
                ? "<h1\(style(format, heading(format, format.TitleSize)))>\(escape(guide.Title))</h1>\n"
                : "<div\(style(format, heading(format, format.TitleSize)))><b>\(escape(guide.Title))</b></div>\n"
        }

        if format.IncludeSummary, !guide.Summary.isEmpty {
            body += "<div\(style(format, bodyStyle(format)))>\(escape(guide.Summary))</div>\n"
        }

        if format.IncludeMeta {
            var facts: [String] = []
            if !guide.Author.isEmpty { facts.append("By " + escape(guide.Author)) }

            let stamp = DateFormatter()
            stamp.dateFormat = "d MMMM yyyy"
            facts.append(stamp.string(from: guide.Updated))

            let count = guide.visible.filter { $0.Kind != .heading }.count
            facts.append(count == 1 ? "1 step" : "\(count) steps")

            // A numeric entity rather than a named one, because a system that treats the
            // markup as strict xml will reject anything outside the five it knows.
            body += "<div\(style(format, meta(format)))>\(facts.joined(separator: " &#160;\u{00B7}&#160; "))</div>\n"
        }

        var listOpen = false

        func openList() {
            if format.UseOrderedList, !listOpen {
                body += "<ol\(style(format, listStyle(format)))>\n"
                listOpen = true
            }
        }

        func closeList() {
            if listOpen {
                body += "</ol>\n"
                listOpen = false
            }
        }

        openList()

        for step in guide.visible {
            if step.Kind == .heading {
                closeList()

                body += format.UseHeadingTags
                    ? "<h2\(style(format, heading(format, format.HeadingSize)))>\(escape(step.Text))</h2>\n"
                    : "<div\(style(format, heading(format, format.HeadingSize)))><b>\(escape(step.Text))</b></div>\n"

                openList()
                continue
            }

            number += 1
            var text = escape(step.Text)
            if format.BoldStepText { text = "<b>" + text + "</b>" }

            if format.UseOrderedList {
                body += "  <li\(style(format, item(format)))>\(text)\n"
            } else {
                let label = format.StepPrefix + String(number) + format.StepSuffix
                body += "<div\(style(format, bodyStyle(format)))>\(escape(label)) \(text)</div>\n"
            }

            if !step.Notes.isEmpty {
                body += "    <div\(style(format, note(format)))>\(escape(format.NotePrefix))\(escape(step.Notes))</div>\n"
            }

            if let picture = self.picture(guide, step, settings, format, options, number, collected) {
                body += "    " + picture + "\n"
            }

            if format.UseOrderedList { body += "  </li>\n" }
        }

        closeList()

        if format.IncludeFooter, !options.fragment, !format.FooterText.isEmpty {
            let stamp = DateFormatter()
            stamp.dateFormat = "d MMMM yyyy"
            let footer = format.FooterText.replacingOccurrences(
                of: "{date}",
                with: stamp.string(from: Date()))

            body += "<div\(style(format, footerStyle(format)))>\(escape(footer))</div>\n"
        }

        if format.SingleContainer { body += "</div>\n" }

        // Styles written on each element carry themselves, so no stylesheet is needed.
        if format.InlineStyles {
            return options.fragment ? body : page(guide, body, "")
        }

        let sheet = stylesheet(format)
        return options.fragment
            ? "<style>\n" + sheet + "\n</style>\n" + body
            : page(guide, body, pageCss + "\n" + sheet)
    }

    static func export(
        guide: Guide,
        settings: Settings,
        to url: URL,
        options: HtmlOptions) throws {
        var options = options
        let format = options.format ?? FormatProfiles.standard()

        if !(options.embedImages ?? format.EmbedImages) {
            // The folder carries the document name, so two guides exported side by side
            // cannot overwrite each other's pictures.
            let name = url.deletingPathExtension().lastPathComponent + " images"
            options.imageFolderName = name
            options.imageFolder = url.deletingLastPathComponent().appendingPathComponent(name)
        }

        let html = build(guide: guide, settings: settings, options: options)
        try html.write(to: url, atomically: true, encoding: .utf8)
    }

    // ------------------------------------------------------------------ pictures

    private static func picture(
        _ guide: Guide,
        _ step: Step,
        _ settings: Settings,
        _ format: FormatProfile,
        _ options: HtmlOptions,
        _ number: Int,
        _ collected: CollectedImages?) -> String? {
        guard step.hasImage else { return nil }

        var data: Data?
        var suffix = "png"
        var mime = "image/png"

        // An animated step is written as an animation where the format allows one.
        if step.Animate, format.AllowAnimation, let animation = StepAnimator.build(
            guide: guide,
            step: step,
            settings: settings,
            motion: GifMotion(rawValue: settings.gifMotion) ?? .normal,
            maxWidth: settings.gifWidth) {
            data = animation
            suffix = "gif"
            mime = "image/gif"
        } else if let rendered = Renderer.render(
            guide: guide,
            step: step,
            settings: settings,
            maxWidth: format.ImageWidth) {
            if format.UseJpeg {
                data = ImageFile.jpegData(rendered, quality: format.JpegQuality / 100)
                suffix = "jpg"
                mime = "image/jpeg"
            } else {
                data = ImageFile.pngData(rendered)
            }
        }

        guard let bytes = data else { return nil }
        collected?.add(number, bytes)

        // Some systems keep pictures beside the page and refer to them by name.
        if !format.ImagePlaceholder.isEmpty {
            return format.ImagePlaceholder.replacingOccurrences(
                of: "{n}",
                with: String(format: "%03d", number))
        }

        if options.collectImagesOnly { return nil }

        var source: String

        if options.embedImages ?? format.EmbedImages, options.imageFolder == nil {
            source = "data:\(mime);base64," + bytes.base64EncodedString()
        } else if let folder = options.imageFolder {
            try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let name = String(format: "step%03d.%@", number, suffix)
            try? bytes.write(to: folder.appendingPathComponent(name))

            let escaped = options.imageFolderName.addingPercentEncoding(
                withAllowedCharacters: .urlPathAllowed) ?? options.imageFolderName
            source = escaped + "/" + name
        } else {
            source = "data:\(mime);base64," + bytes.base64EncodedString()
        }

        let classAttribute = format.InlineStyles ? "" : " class=\"sw-shot\""
        return "<img\(classAttribute)\(style(format, image(format))) src=\"\(source)\" alt=\"\(escape(step.Text))\" />"
    }

    // ------------------------------------------------------------------ styling

    /// Writes the style on the element when the format asks for it, and the class name
    /// otherwise, so the same builder serves both kinds of document.
    private static func style(_ format: FormatProfile, _ rules: (name: String, inline: String)) -> String {
        if format.InlineStyles {
            return rules.inline.isEmpty ? "" : " style=\"\(rules.inline)\""
        }

        return rules.name.isEmpty ? "" : " class=\"\(rules.name)\""
    }

    private static func font(_ format: FormatProfile) -> String {
        format.FontFamily.isEmpty ? "" : "font-family:\(format.FontFamily);"
    }

    /// How the less important text is held back. A format that is not allowed to state a
    /// colour fades it instead, so the receiving system keeps control of light and dark mode.
    private static func quiet(_ format: FormatProfile) -> String {
        format.AllowColor ? "color:#6c7480;" : "opacity:.7;"
    }

    private static func container(_ format: FormatProfile) -> (String, String) {
        ("sw-doc", font(format) + "font-size:\(format.BodySize)px;")
    }

    private static func heading(_ format: FormatProfile, _ size: Int) -> (String, String) {
        ("sw-section", font(format) + "font-size:\(size)px;font-weight:bold;margin-bottom:\(format.BlockSpacing)px;")
    }

    private static func bodyStyle(_ format: FormatProfile) -> (String, String) {
        ("sw-text", font(format) + "font-size:\(format.BodySize)px;margin-bottom:\(format.BlockSpacing)px;")
    }

    private static func note(_ format: FormatProfile) -> (String, String) {
        ("sw-note", font(format) + "font-size:\(format.NoteSize)px;margin-bottom:\(format.BlockSpacing)px;" + quiet(format))
    }

    private static func meta(_ format: FormatProfile) -> (String, String) {
        ("sw-meta", font(format) + "font-size:\(format.MetaSize)px;margin-bottom:\(format.BlockSpacing)px;" + quiet(format))
    }

    private static func footerStyle(_ format: FormatProfile) -> (String, String) {
        ("sw-foot", font(format) + "font-size:\(format.MetaSize)px;margin-top:18px;padding-top:8px;border-top:1px solid;" + quiet(format))
    }

    private static func listStyle(_ format: FormatProfile) -> (String, String) {
        ("sw-steps", font(format) + "font-size:\(format.BodySize)px;margin-top:0px;margin-bottom:\(format.BlockSpacing)px;")
    }

    private static func item(_ format: FormatProfile) -> (String, String) {
        ("sw-step", "margin-bottom:\(format.BlockSpacing)px;")
    }

    private static func image(_ format: FormatProfile) -> (String, String) {
        var inline = "display:block;height:auto;max-width:100%;"
        if format.ImageDisplayWidth > 0 { inline += "width:\(format.ImageDisplayWidth)px;" }
        inline += "margin-top:8px;margin-bottom:\(format.BlockSpacing)px;"
        if format.RoundImageCorners { inline += "border-radius:10px;" }
        return ("sw-shot", inline)
    }

    /// The stylesheet used when the rules are not written onto each element.
    private static func stylesheet(_ format: FormatProfile) -> String {
        let family = format.FontFamily.isEmpty ? "system-ui, sans-serif" : format.FontFamily
        let faded = format.AllowColor ? "color: #6c7480;" : "opacity: 0.75;"

        var css = ""
        css += ".sw-doc { max-width: 860px; margin: 0 auto; padding: 40px 24px 72px; font-family: \(family); font-size: \(format.BodySize)px; line-height: 1.55; }\n"
        css += ".sw-doc h1 { font-size: \(format.TitleSize)px; line-height: 1.2; margin: 0 0 12px; }\n"
        css += ".sw-section { font-size: \(format.HeadingSize)px; margin: 32px 0 10px; }\n"
        css += ".sw-meta { font-size: \(format.MetaSize)px; \(faded) margin-bottom: 24px; }\n"
        css += ".sw-steps { margin-top: 0; margin-bottom: \(format.BlockSpacing)px; padding-left: 22px; }\n"
        css += ".sw-step { margin-bottom: \(format.BlockSpacing + 12)px; }\n"
        css += ".sw-note { font-size: \(format.NoteSize)px; \(faded) margin: 6px 0 \(format.BlockSpacing)px; }\n"

        var picture = ".sw-shot { display: block; height: auto; max-width: 100%;"
        if format.ImageDisplayWidth > 0 { picture += " width: \(format.ImageDisplayWidth)px;" }
        picture += " margin: 8px 0 \(format.BlockSpacing)px;"
        if format.RoundImageCorners {
            picture += " border: 1px solid rgba(128,128,128,0.3); border-radius: 10px;"
        }

        css += picture + " }\n"
        css += ".sw-foot { font-size: \(format.MetaSize)px; \(faded) margin-top: 40px; padding-top: 12px; border-top: 1px solid rgba(128,128,128,0.3); }\n"
        css += "@media print { .sw-step { break-inside: avoid; page-break-inside: avoid; } .sw-foot { display: none; } }\n"

        if format.AllowColor {
            css += ".sw-num { background: #2563eb; color: #fff; }\n"
        }

        return css
    }

    private static func page(_ guide: Guide, _ body: String, _ css: String) -> String {
        var head = ""
        if !css.isEmpty {
            head = "<style>\n" + css + "\n</style>\n"
        }

        return """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>\(escape(guide.Title))</title>
        \(head)</head>
        <body>
        \(body)
        </body>
        </html>
        """
    }

    private static let pageCss = """
    :root { color-scheme: light dark; }
    body { margin: 0; background: #f6f7f9; }
    @media (prefers-color-scheme: dark) { body { background: #14161a; } }
    """

    static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }
}

// ---------------------------------------------------------------------- markdown

enum MarkdownExporter {
    static func export(guide: Guide, settings: Settings, to url: URL) throws {
        let folderName = url.deletingPathExtension().lastPathComponent + " images"
        let imageFolder = url.deletingLastPathComponent().appendingPathComponent(folderName)

        var text = "# \(guide.Title)\n\n"

        if !guide.Summary.isEmpty { text += guide.Summary + "\n\n" }

        var facts: [String] = []
        if !guide.Author.isEmpty { facts.append("By " + guide.Author) }

        let stamp = DateFormatter()
        stamp.dateFormat = "d MMMM yyyy"
        facts.append(stamp.string(from: guide.Updated))
        text += "*" + facts.joined(separator: " \u{00B7} ") + "*\n\n"

        var number = 0

        for step in guide.visible {
            if step.Kind == .heading {
                text += "\n## \(step.Text)\n\n"
                continue
            }

            number += 1
            text += "**Step \(number).** \(step.Text)\n\n"

            if !step.Notes.isEmpty {
                text += "> " + step.Notes.replacingOccurrences(of: "\n", with: "\n> ") + "\n\n"
            }

            guard step.hasImage else { continue }

            var data: Data?
            var suffix = "png"

            if step.Animate, let animation = StepAnimator.build(
                guide: guide,
                step: step,
                settings: settings,
                motion: GifMotion(rawValue: settings.gifMotion) ?? .normal,
                maxWidth: settings.gifWidth) {
                data = animation
                suffix = "gif"
            } else if let picture = Renderer.render(guide: guide, step: step, settings: settings, maxWidth: 1400) {
                data = ImageFile.pngData(picture)
            }

            guard let bytes = data else { continue }

            try FileManager.default.createDirectory(at: imageFolder, withIntermediateDirectories: true)
            let name = String(format: "step%03d.%@", number, suffix)
            try bytes.write(to: imageFolder.appendingPathComponent(name))

            let escaped = folderName.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? folderName
            text += "![Step \(number)](\(escaped)/\(name))\n\n"
        }

        try text.write(to: url, atomically: true, encoding: .utf8)
    }

    static func plainText(_ guide: Guide) -> String {
        var text = guide.Title + "\n"
        text += String(repeating: "=", count: min(60, max(4, guide.Title.count))) + "\n\n"

        if !guide.Summary.isEmpty { text += guide.Summary + "\n\n" }

        var number = 0

        for step in guide.visible {
            if step.Kind == .heading {
                text += "\n" + step.Text.uppercased() + "\n\n"
                continue
            }

            number += 1
            text += "\(number). \(step.Text)\n"
            if !step.Notes.isEmpty { text += "   " + step.Notes + "\n" }
        }

        return text
    }
}
