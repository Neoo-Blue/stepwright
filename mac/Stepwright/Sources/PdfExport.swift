import AppKit
import CoreGraphics
import Foundation

/// Writes the guide as a document. The platform draws the pages, so the text stays
/// selectable and searchable rather than being flattened into pictures.
enum PdfExporter {
    private static let pageWidth: CGFloat = 595.28   // A4 in points
    private static let pageHeight: CGFloat = 841.89
    private static let margin: CGFloat = 54

    static func export(guide: Guide, settings: Settings, to url: URL) throws {
        let output = NSMutableData()

        guard let consumer = CGDataConsumer(data: output as CFMutableData) else {
            throw Failure.message("The document could not be started.")
        }

        var mediaBox = CGRect(x: 0, y: 0, width: pageWidth, height: pageHeight)

        guard let context = CGContext(consumer: consumer, mediaBox: &mediaBox, nil) else {
            throw Failure.message("The document could not be started.")
        }

        var cursor = pageHeight - margin
        var pageOpen = false

        func newPage() {
            if pageOpen { context.endPDFPage() }
            context.beginPDFPage(nil)
            pageOpen = true
            cursor = pageHeight - margin
        }

        func ensure(_ height: CGFloat) {
            if !pageOpen { newPage(); return }
            if cursor - height < margin { newPage() }
        }

        func draw(_ text: String, font: NSFont, color: NSColor, spaceBefore: CGFloat = 0, spaceAfter: CGFloat = 0, indent: CGFloat = 0) {
            guard !text.isEmpty else { return }

            let attributes: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: color]
            let string = NSAttributedString(string: text, attributes: attributes)
            let width = pageWidth - (margin * 2) - indent

            let measured = string.boundingRect(
                with: CGSize(width: width, height: .greatestFiniteMagnitude),
                options: [.usesLineFragmentOrigin, .usesFontLeading])

            let height = ceil(measured.height)
            cursor -= spaceBefore
            ensure(height)
            cursor -= height

            let previous = NSGraphicsContext.current
            NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
            string.draw(with: CGRect(x: margin + indent, y: cursor, width: width, height: height),
                        options: [.usesLineFragmentOrigin, .usesFontLeading])
            NSGraphicsContext.current = previous

            cursor -= spaceAfter
        }

        func rule() {
            cursor -= 8
            ensure(2)
            context.setStrokeColor(NSColor(white: 0.85, alpha: 1).cgColor)
            context.setLineWidth(0.7)
            context.move(to: CGPoint(x: margin, y: cursor))
            context.addLine(to: CGPoint(x: pageWidth - margin, y: cursor))
            context.strokePath()
            cursor -= 12
        }

        newPage()

        draw(guide.Title, font: .systemFont(ofSize: 21, weight: .bold), color: .black, spaceAfter: 6)

        if !guide.Summary.isEmpty {
            draw(guide.Summary, font: .systemFont(ofSize: 11.5), color: NSColor(white: 0.28, alpha: 1), spaceAfter: 4)
        }

        var facts: [String] = []
        if !guide.Author.isEmpty { facts.append("By " + guide.Author) }

        let stamp = DateFormatter()
        stamp.dateFormat = "d MMMM yyyy"
        facts.append(stamp.string(from: guide.Updated))

        let total = guide.visible.filter { $0.Kind != .heading }.count
        facts.append(total == 1 ? "1 step" : "\(total) steps")

        draw(facts.joined(separator: "   "), font: .systemFont(ofSize: 8.6), color: NSColor(white: 0.52, alpha: 1))
        rule()

        var number = 0

        for step in guide.visible {
            if step.Kind == .heading {
                draw(step.Text, font: .systemFont(ofSize: 14, weight: .bold), color: .black, spaceBefore: 10, spaceAfter: 6)
                continue
            }

            number += 1

            // A step and its picture belong on the same page, so the room for both is
            // claimed before either one is drawn.
            var picture: CGImage?
            var pictureHeight: CGFloat = 0

            if step.hasImage {
                picture = Renderer.render(guide: guide, step: step, settings: settings, maxWidth: 1500)

                if let picture {
                    let width = pageWidth - (margin * 2)
                    pictureHeight = width * CGFloat(picture.height) / CGFloat(max(1, picture.width))
                    pictureHeight = min(pictureHeight, pageHeight - (margin * 2))
                }
            }

            ensure(pictureHeight + 60)

            draw("\(number).  \(step.Text)", font: .systemFont(ofSize: 11.5, weight: .semibold), color: .black, spaceBefore: 10, spaceAfter: 6)

            if !step.Notes.isEmpty {
                draw(step.Notes, font: .systemFont(ofSize: 10), color: NSColor(white: 0.35, alpha: 1), spaceAfter: 4, indent: 16)
            }

            if let picture {
                ensure(pictureHeight)
                cursor -= pictureHeight

                let width = pageWidth - (margin * 2)
                context.interpolationQuality = .high
                context.draw(picture, in: CGRect(x: margin, y: cursor, width: width, height: pictureHeight))
                cursor -= 12
            }
        }

        cursor -= 6
        rule()
        draw("Made with Stepwright", font: .systemFont(ofSize: 8), color: NSColor(white: 0.6, alpha: 1))

        if pageOpen { context.endPDFPage() }
        context.closePDF()

        try (output as Data).write(to: url)
    }
}
