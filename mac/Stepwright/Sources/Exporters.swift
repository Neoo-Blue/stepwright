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
    var fragment = false
    var embedImages = true
    var useJpeg = false
    var allowAnimation = true
    var maxImageWidth = 1400
    var jpegQuality = 0.82
    var imageFolder: URL?
    var imageFolderName = "images"
}

enum HtmlExporter {
    static func build(guide: Guide, settings: Settings, options: HtmlOptions) -> String {
        var options = options
        var body = ""
        var number = 0

        body += "<div class=\"sw-doc\">\n<header class=\"sw-head\">\n"
        body += "  <h1>\(escape(guide.Title))</h1>\n"

        if !guide.Summary.isEmpty {
            body += "  <p class=\"sw-summary\">\(escape(guide.Summary))</p>\n"
        }

        var facts: [String] = []
        if !guide.Author.isEmpty { facts.append("By " + escape(guide.Author)) }

        let stamp = DateFormatter()
        stamp.dateFormat = "d MMMM yyyy"
        facts.append(stamp.string(from: guide.Updated))

        let count = guide.visible.filter { $0.Kind != .heading }.count
        facts.append(count == 1 ? "1 step" : "\(count) steps")

        body += "  <p class=\"sw-meta\">\(facts.joined(separator: " &nbsp;\u{00B7}&nbsp; "))</p>\n"
        body += "</header>\n<ol class=\"sw-steps\">\n"

        for step in guide.visible {
            if step.Kind == .heading {
                body += "</ol>\n<h2 class=\"sw-section\">\(escape(step.Text))</h2>\n<ol class=\"sw-steps\">\n"
                continue
            }

            number += 1
            body += "  <li class=\"sw-step\">\n    <div class=\"sw-row\">\n"
            body += "      <div class=\"sw-num\">\(number)</div>\n"
            body += "      <div class=\"sw-text\">\(escape(step.Text))</div>\n    </div>\n"

            if !step.Notes.isEmpty {
                body += "    <p class=\"sw-note\">\(escape(step.Notes))</p>\n"
            }

            if let source = imageSource(guide, step, settings, &options, number) {
                body += "    <img class=\"sw-shot\" src=\"\(source)\" alt=\"\(escape(step.Text))\" loading=\"lazy\" />\n"
            }

            body += "  </li>\n"
        }

        body += "</ol>\n"

        if !options.fragment {
            body += "<footer class=\"sw-foot\">Made with Stepwright</footer>\n"
        }

        body += "</div>\n"

        if options.fragment {
            // No page level rules, so pasting this cannot repaint the page it lands in.
            return "<style>\n" + documentCss + "\n</style>\n" + body
        }

        return """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8" />
        <meta name="viewport" content="width=device-width, initial-scale=1" />
        <title>\(escape(guide.Title))</title>
        <style>
        \(pageCss)
        \(documentCss)
        </style>
        </head>
        <body>
        \(body)
        </body>
        </html>
        """
    }

    static func export(guide: Guide, settings: Settings, to url: URL, options: HtmlOptions) throws {
        var options = options

        if !options.embedImages {
            let name = url.deletingPathExtension().lastPathComponent + " images"
            options.imageFolderName = name
            options.imageFolder = url.deletingLastPathComponent().appendingPathComponent(name)
        }

        let html = build(guide: guide, settings: settings, options: options)
        try html.write(to: url, atomically: true, encoding: .utf8)
    }

    private static func imageSource(
        _ guide: Guide,
        _ step: Step,
        _ settings: Settings,
        _ options: inout HtmlOptions,
        _ number: Int) -> String? {
        guard step.hasImage else { return nil }

        var data: Data?
        var mime = "image/png"
        var suffix = "png"

        if step.Animate, options.allowAnimation,
           let animation = StepAnimator.build(
            guide: guide,
            step: step,
            settings: settings,
            motion: GifMotion(rawValue: settings.gifMotion) ?? .normal,
            maxWidth: settings.gifWidth) {
            data = animation
            mime = "image/gif"
            suffix = "gif"
        } else if let picture = Renderer.render(
            guide: guide,
            step: step,
            settings: settings,
            maxWidth: options.maxImageWidth) {
            if options.useJpeg {
                data = ImageFile.jpegData(picture, quality: options.jpegQuality)
                mime = "image/jpeg"
                suffix = "jpg"
            } else {
                data = ImageFile.pngData(picture)
            }
        }

        guard let bytes = data else { return nil }

        if options.embedImages || options.imageFolder == nil {
            return "data:\(mime);base64,\(bytes.base64EncodedString())"
        }

        guard let folder = options.imageFolder else { return nil }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let name = String(format: "step%03d.%@", number, suffix)
        try? bytes.write(to: folder.appendingPathComponent(name))

        let escaped = options.imageFolderName.addingPercentEncoding(
            withAllowedCharacters: .urlPathAllowed) ?? options.imageFolderName
        return escaped + "/" + name
    }

    static func escape(_ value: String) -> String {
        value.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
    }

    private static let pageCss = """
    :root { color-scheme: light dark; }
    body { margin: 0; background: #f6f7f9; }
    @media (prefers-color-scheme: dark) { body { background: #14161a; } }
    """

    private static let documentCss = """
    .sw-doc { max-width: 860px; margin: 0 auto; padding: 48px 24px 80px;
      font-family: -apple-system, "SF Pro Text", "Segoe UI", system-ui, Helvetica, Arial, sans-serif;
      color: #16181d; line-height: 1.55; }
    .sw-head { border-bottom: 1px solid #e3e6ea; padding-bottom: 20px; margin-bottom: 28px; }
    .sw-head h1 { font-size: 32px; line-height: 1.2; margin: 0 0 10px; }
    .sw-summary { margin: 0 0 10px; font-size: 17px; color: #3c414a; }
    .sw-meta { margin: 0; font-size: 13px; color: #767d88; text-transform: uppercase; letter-spacing: 0.6px; }
    .sw-section { font-size: 20px; margin: 40px 0 8px; }
    .sw-steps { list-style: none; margin: 0; padding: 0; }
    .sw-step { margin: 0 0 30px; padding: 0; }
    .sw-row { display: flex; align-items: flex-start; gap: 14px; }
    .sw-num { flex: 0 0 auto; width: 30px; height: 30px; border-radius: 50%;
      background: #2563eb; color: #fff; font-size: 15px; font-weight: 650;
      display: flex; align-items: center; justify-content: center; margin-top: 1px; }
    .sw-text { font-size: 17px; font-weight: 550; padding-top: 3px; }
    .sw-note { margin: 8px 0 0 44px; font-size: 15px; color: #4b515c; }
    .sw-shot { display: block; width: 100%; height: auto; margin: 14px 0 0 44px;
      max-width: calc(100% - 44px); border: 1px solid #dfe3e8; border-radius: 10px;
      box-shadow: 0 2px 10px rgba(15, 20, 30, 0.08); }
    .sw-foot { margin-top: 48px; padding-top: 16px; border-top: 1px solid #e3e6ea;
      font-size: 12px; color: #8b929c; text-align: center; }
    @media (prefers-color-scheme: dark) {
      .sw-doc { color: #e8eaee; }
      .sw-head, .sw-foot { border-color: #2a2e36; }
      .sw-summary { color: #b9bfc9; }
      .sw-note { color: #b0b6c0; }
      .sw-shot { border-color: #2a2e36; box-shadow: none; }
    }
    @media print {
      .sw-doc { max-width: none; padding: 0; background: #fff; }
      .sw-step { break-inside: avoid; page-break-inside: avoid; }
      .sw-foot { display: none; }
    }
    """
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
