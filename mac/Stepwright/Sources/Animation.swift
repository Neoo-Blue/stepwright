import AppKit
import CoreGraphics
import ImageIO
import Foundation

enum GifMotion: String {
    case gentle = "Gentle"
    case normal = "Normal"
    case quick = "Quick"
}

/// Writes an animation. The platform already knows this format, so there is no encoder here,
/// only the decision of what each picture holds and how long it stays.
enum GifFile {
    static func data(frames: [(image: CGImage, delay: Double)]) -> Data? {
        guard !frames.isEmpty else { return nil }

        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            output as CFMutableData,
            "com.compuserve.gif" as CFString,
            frames.count,
            nil) else { return nil }

        // Zero means loop forever.
        let fileProperties = [
            kCGImagePropertyGIFDictionary as String: [
                kCGImagePropertyGIFLoopCount as String: 0,
            ],
        ] as CFDictionary

        CGImageDestinationSetProperties(destination, fileProperties)

        for frame in frames {
            let frameProperties = [
                kCGImagePropertyGIFDictionary as String: [
                    kCGImagePropertyGIFUnclampedDelayTime as String: frame.delay,
                    kCGImagePropertyGIFDelayTime as String: frame.delay,
                ],
            ] as CFDictionary

            CGImageDestinationAddImage(destination, frame.image, frameProperties)
        }

        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }
}

/// Turns one screenshot into a short movement that starts wide, so the reader can see where
/// they are, and settles on the control that was used.
///
/// It is built from the picture already captured, so there is nothing to time or catch, and
/// the same step always produces the same animation.
enum StepAnimator {
    static func canAnimate(_ step: Step) -> Bool {
        guard step.hasImage else { return false }
        if step.ClickPoint != nil { return true }
        if let area = step.ElementArea, !area.isEmpty { return true }
        return false
    }

    static func build(
        guide: Guide,
        step: Step,
        settings: Settings,
        motion: GifMotion = .normal,
        maxWidth: Int = 760) -> Data? {
        guard canAnimate(step),
              let path = guide.imagePath(step),
              let source = ImageFile.load(path) else { return nil }

        // Everything is drawn once onto the whole picture, then the animation is only a
        // moving window over that. The marker and any callouts come along for free.
        let whole = Step()
        whole.ClickPoint = step.ClickPoint
        whole.ElementArea = step.ElementArea
        whole.WindowArea = step.WindowArea
        whole.Annotations = step.Annotations
        whole.ShowClickMarker = step.ShowClickMarker
        whole.ShowElementOutline = step.ShowElementOutline
        whole.AutoZoom = false

        guard let composed = Renderer.compose(
            step: whole,
            source: source,
            markerHex: settings.markerColor,
            padding: settings.zoomPadding) else { return nil }

        let size = CGSize(width: composed.width, height: composed.height)
        var (start, end) = framings(step: step, size: size, padding: settings.zoomPadding)

        if end.width < 40 || end.height < 40 { return nil }
        start = matchAspect(start, aspect: end.width / max(1, end.height), bounds: size)

        let timing = self.timing(motion)
        let width = min(maxWidth, Int(end.width))
        let height = max(1, Int((Double(width) * end.height / max(1, end.width)).rounded()))

        var frames: [(image: CGImage, delay: Double)] = []

        for index in 0..<timing.steps {
            let t = ease(Double(index) / Double(max(1, timing.steps - 1)))
            guard let picture = slice(composed, between(start, end, t), width, height) else { continue }
            frames.append((picture, index == timing.steps - 1 ? timing.hold : timing.move))
        }

        for index in 1...max(1, timing.back) {
            let t = ease(1 - (Double(index) / Double(max(1, timing.back))))
            guard let picture = slice(composed, between(start, end, t), width, height) else { continue }
            frames.append((picture, index == timing.back ? max(0.4, timing.hold / 3) : timing.move))
        }

        return GifFile.data(frames: frames)
    }

    /// Where the movement starts and where it settles.
    private static func framings(step: Step, size: CGSize, padding: Int) -> (CGRect, CGRect) {
        var end = Renderer.variantCrop(step: step, imageSize: size, variant: .focus, padding: padding)
        let full = CGRect(origin: .zero, size: size)

        // Start from the window when there is one, because a whole desktop is rarely useful.
        var start = Renderer.variantCrop(step: step, imageSize: size, variant: .window, padding: padding)
        if start.width < end.width * 1.35 || start.height < end.height * 1.35 {
            start = full
        }

        // Nothing to move towards, so tighten the ending instead of animating in place.
        if end.width > start.width * 0.85 && end.height > start.height * 0.85 {
            end = Renderer.variantCrop(step: step, imageSize: size, variant: .close, padding: padding)
        }

        return (start, end)
    }

    private static func timing(_ motion: GifMotion) -> (steps: Int, move: Double, hold: Double, back: Int) {
        switch motion {
        case .gentle:
            return (14, 0.07, 2.2, 8)
        case .quick:
            return (8, 0.04, 1.3, 5)
        case .normal:
            return (11, 0.05, 1.8, 6)
        }
    }

    private static func ease(_ t: Double) -> Double {
        let clamped = min(max(t, 0), 1)
        return clamped < 0.5 ? 2 * clamped * clamped : 1 - (2 * (1 - clamped) * (1 - clamped))
    }

    private static func between(_ from: CGRect, _ to: CGRect, _ t: Double) -> CGRect {
        CGRect(
            x: from.origin.x + ((to.origin.x - from.origin.x) * t),
            y: from.origin.y + ((to.origin.y - from.origin.y) * t),
            width: max(8, from.width + ((to.width - from.width) * t)),
            height: max(8, from.height + ((to.height - from.height) * t)))
    }

    /// Grows a region until it has the required shape, then keeps it on the picture.
    private static func matchAspect(_ area: CGRect, aspect: CGFloat, bounds: CGSize) -> CGRect {
        var width = area.width
        var height = area.height

        if width / max(1, height) > aspect {
            height = width / max(0.01, aspect)
        } else {
            width = height * aspect
        }

        width = min(width, bounds.width)
        height = min(height, bounds.height)

        var result = CGRect(
            x: area.midX - (width / 2),
            y: area.midY - (height / 2),
            width: width,
            height: height)

        if result.minX < 0 { result.origin.x = 0 }
        if result.minY < 0 { result.origin.y = 0 }
        if result.maxX > bounds.width { result.origin.x = bounds.width - result.width }
        if result.maxY > bounds.height { result.origin.y = bounds.height - result.height }

        return result
    }

    private static func slice(_ image: CGImage, _ region: CGRect, _ width: Int, _ height: Int) -> CGImage? {
        let bounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
        var area = region.intersection(bounds)
        if area.width < 4 || area.height < 4 { area = bounds }

        guard let piece = image.cropping(to: area),
              let context = CGContext(
                data: nil,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }

        context.interpolationQuality = .high
        context.draw(piece, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }
}

/// Strings the whole guide together into one animation: every step in order, each held long
/// enough to read, with its number and a bar showing how far along it is.
enum GuideAnimator {
    static func build(guide: Guide, settings: Settings, maxWidth: Int = 900) -> Data? {
        let steps = guide.visible.filter { $0.Kind != .heading && $0.hasImage }
        guard !steps.isEmpty else { return nil }

        let width = min(max(maxWidth, 480), 1400)
        let captionHeight = 78
        let pictureHeight = Int(Double(width) * 0.58)
        let height = pictureHeight + captionHeight

        var frames: [(image: CGImage, delay: Double)] = []

        for (index, step) in steps.enumerated() {
            let picture = Renderer.render(guide: guide, step: step, settings: settings, maxWidth: width * 2)
            guard let frame = compose(
                picture: picture,
                step: step,
                number: index + 1,
                total: steps.count,
                width: width,
                height: height,
                pictureHeight: pictureHeight) else { continue }

            frames.append((frame, holdFor(step)))
        }

        return GifFile.data(frames: frames)
    }

    /// Long enough to read the sentence, and never so long it feels stuck.
    private static func holdFor(_ step: Step) -> Double {
        let words = step.Text.split(separator: " ").count
        return min(max(1.3 + (Double(words) * 0.14), 1.3), 4.2)
    }

    private static func compose(
        picture: CGImage?,
        step: Step,
        number: Int,
        total: Int,
        width: Int,
        height: Int,
        pictureHeight: Int) -> CGImage? {
        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return nil }

        let backdrop = NSColor(srgbRed: 24 / 255, green: 26 / 255, blue: 31 / 255, alpha: 1)
        context.setFillColor(backdrop.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))

        // The caption sits at the bottom of the picture, which in this context is the
        // lower part of the drawing, so the picture is placed above it.
        if let picture {
            let scale = min(
                Double(width - 24) / Double(picture.width),
                Double(pictureHeight - 20) / Double(picture.height))

            let drawWidth = max(1, Int(Double(picture.width) * scale))
            let drawHeight = max(1, Int(Double(picture.height) * scale))

            let area = CGRect(
                x: (width - drawWidth) / 2,
                y: height - pictureHeight + ((pictureHeight - 20 - drawHeight) / 2) + 10,
                width: drawWidth,
                height: drawHeight)

            context.interpolationQuality = .high
            context.draw(picture, in: area)

            context.setStrokeColor(NSColor(white: 0.3, alpha: 1).cgColor)
            context.setLineWidth(1)
            context.stroke(area)
        }

        let accent = NSColor(srgbRed: 88 / 255, green: 132 / 255, blue: 255 / 255, alpha: 1)
        let badge = CGRect(x: 20, y: height - pictureHeight - 44, width: 26, height: 26)

        context.setFillColor(accent.cgColor)
        context.fillEllipse(in: badge)

        let previous = NSGraphicsContext.current
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)

        let numberText = NSAttributedString(string: "\(number)", attributes: [
            .font: NSFont.systemFont(ofSize: 13, weight: .bold),
            .foregroundColor: NSColor.white,
        ])

        let numberSize = numberText.size()
        numberText.draw(at: CGPoint(
            x: badge.midX - (numberSize.width / 2),
            y: badge.midY - (numberSize.height / 2)))

        let sentence = NSAttributedString(string: step.Text, attributes: [
            .font: NSFont.systemFont(ofSize: 16),
            .foregroundColor: NSColor(white: 0.93, alpha: 1),
        ])

        sentence.draw(in: CGRect(
            x: 56,
            y: height - pictureHeight - 52,
            width: Double(width - 130),
            height: 40))

        let counter = NSAttributedString(string: "\(number) of \(total)", attributes: [
            .font: NSFont.systemFont(ofSize: 12),
            .foregroundColor: NSColor(white: 0.6, alpha: 1),
        ])

        let counterSize = counter.size()
        counter.draw(at: CGPoint(x: Double(width) - counterSize.width - 18, y: Double(height - pictureHeight - 40)))

        NSGraphicsContext.current = previous

        context.setFillColor(NSColor(white: 0.22, alpha: 1).cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width, height: 4))

        context.setFillColor(accent.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: width * number / max(1, total), height: 4))

        return context.makeImage()
    }
}
