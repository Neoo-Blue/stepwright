import AppKit
import CoreGraphics
import Foundation

/// The framings a person can pick between for a step.
enum CropVariant {
    case full
    case window
    case focus
    case close
}

/// Draws the finished picture for a step: crop, redactions, callouts and the click marker.
///
/// Everything the recorder stores is measured from the top left of the screenshot, while a
/// drawing context measures from the bottom left, so shapes are flipped on the way out.
enum Renderer {
    static func compose(
        step: Step,
        source: CGImage,
        markerHex: String,
        padding: Int) -> CGImage? {
        let size = CGSize(width: source.width, height: source.height)
        var crop = effectiveCrop(step: step, imageSize: size, padding: padding)

        if crop.width < 8 || crop.height < 8 {
            crop = CGRect(origin: .zero, size: size)
        }

        let width = Int(crop.width.rounded())
        let height = Int(crop.height.rounded())

        guard let cropped = source.cropping(to: crop),
              let context = CGContext(
                data: nil,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: 0,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }

        context.interpolationQuality = .high
        context.draw(cropped, in: CGRect(x: 0, y: 0, width: width, height: height))

        let marker = NSColor.fromHex(markerHex)

        // Redactions first, so nothing sensitive can end up under a callout.
        for annotation in step.Annotations where annotation.Kind == .blur {
            pixelate(context, source: source, area: annotation.Area.rect, crop: crop, height: height)
        }

        if step.ShowElementOutline,
           let area = step.ElementArea,
           isUsefulElement(area.rect, imageSize: size, click: step.ClickPoint, maxShare: 0.55) {
            drawOutline(context, rect: shift(area.rect, crop, height), color: marker)
        }

        for annotation in step.Annotations where annotation.Kind != .blur {
            draw(annotation, in: context, crop: crop, height: height)
        }

        if step.ShowClickMarker, let click = step.ClickPoint {
            let point = shift(click.point, crop, height)
            drawMarker(context, at: point, color: marker)
        }

        return context.makeImage()
    }

    // ------------------------------------------------------------------ framing

    static func effectiveCrop(step: Step, imageSize: CGSize, padding: Int) -> CGRect {
        let full = CGRect(origin: .zero, size: imageSize)

        if let manual = step.Crop, !manual.isEmpty {
            return manual.rect.intersection(full)
        }

        if !step.AutoZoom { return full }

        var anchor = CGRect.zero

        if let area = step.ElementArea,
           isUsefulElement(area.rect, imageSize: imageSize, click: step.ClickPoint, maxShare: 0.72) {
            anchor = area.rect
        }

        if anchor.isEmpty, let click = step.ClickPoint {
            anchor = CGRect(x: click.point.x - 50, y: click.point.y - 50, width: 100, height: 100)
        }

        if anchor.isEmpty { return full }

        let padX = CGFloat(max(80, padding))
        let padY = padX * 0.62
        let box = anchor.insetBy(dx: -padX, dy: -padY)

        // Keep the shape of the source so every exported picture lines up.
        let aspect = imageSize.width / max(1, imageSize.height)
        var height = max(box.height, box.width / aspect)
        var width = height * aspect

        if width >= imageSize.width * 0.86 || height >= imageSize.height * 0.86 { return full }

        width = min(width, imageSize.width)
        height = min(height, imageSize.height)

        var result = CGRect(
            x: anchor.midX - (width / 2),
            y: anchor.midY - (height / 2),
            width: width,
            height: height)

        if result.minX < 0 { result.origin.x = 0 }
        if result.minY < 0 { result.origin.y = 0 }
        if result.maxX > imageSize.width { result.origin.x = imageSize.width - result.width }
        if result.maxY > imageSize.height { result.origin.y = imageSize.height - result.height }

        return result.intersection(full)
    }

    static func variantCrop(
        step: Step,
        imageSize: CGSize,
        variant: CropVariant,
        padding: Int) -> CGRect {
        let full = CGRect(origin: .zero, size: imageSize)

        switch variant {
        case .window:
            if let window = step.WindowArea, !window.isEmpty {
                let area = window.rect.intersection(full)
                if area.width > 200, area.height > 150 { return area }
            }

            return full

        case .focus, .close:
            let probe = Step()
            probe.ClickPoint = step.ClickPoint
            probe.ElementArea = step.ElementArea
            probe.AutoZoom = true

            let space = variant == .close ? max(60, padding / 3) : padding
            return effectiveCrop(step: probe, imageSize: imageSize, padding: space)

        case .full:
            return full
        }
    }

    /// True when a reported rectangle is worth showing: sensible size, not most of the
    /// screen, and actually containing the point that was clicked.
    static func isUsefulElement(
        _ area: CGRect,
        imageSize: CGSize,
        click: PointI?,
        maxShare: CGFloat) -> Bool {
        if area.width <= 6 || area.height <= 6 { return false }
        if area.width > imageSize.width * maxShare && area.height > imageSize.height * maxShare { return false }
        if area.width >= imageSize.width * 0.98 || area.height >= imageSize.height * 0.98 { return false }

        if let click {
            if !area.insetBy(dx: -6, dy: -6).contains(click.point) { return false }
        }

        return true
    }

    // ------------------------------------------------------------------ drawing

    private static func shift(_ rect: CGRect, _ crop: CGRect, _ height: Int) -> CGRect {
        let x = rect.origin.x - crop.origin.x
        let y = rect.origin.y - crop.origin.y
        return CGRect(x: x, y: CGFloat(height) - y - rect.height, width: rect.width, height: rect.height)
    }

    private static func shift(_ point: CGPoint, _ crop: CGRect, _ height: Int) -> CGPoint {
        CGPoint(x: point.x - crop.origin.x, y: CGFloat(height) - (point.y - crop.origin.y))
    }

    private static func drawOutline(_ context: CGContext, rect: CGRect, color: NSColor) {
        let padded = rect.insetBy(dx: -4, dy: -4)

        context.saveGState()
        context.setStrokeColor(color.withAlphaComponent(0.28).cgColor)
        context.setLineWidth(9)
        context.addPath(CGPath(roundedRect: padded, cornerWidth: 8, cornerHeight: 8, transform: nil))
        context.strokePath()

        context.setStrokeColor(color.withAlphaComponent(0.92).cgColor)
        context.setLineWidth(3)
        context.addPath(CGPath(roundedRect: padded, cornerWidth: 8, cornerHeight: 8, transform: nil))
        context.strokePath()
        context.restoreGState()
    }

    private static func drawMarker(_ context: CGContext, at point: CGPoint, color: NSColor) {
        context.saveGState()

        context.setFillColor(color.withAlphaComponent(0.18).cgColor)
        context.fillEllipse(in: CGRect(x: point.x - 34, y: point.y - 34, width: 68, height: 68))

        context.setStrokeColor(color.withAlphaComponent(0.96).cgColor)
        context.setLineWidth(4)
        context.strokeEllipse(in: CGRect(x: point.x - 20, y: point.y - 20, width: 40, height: 40))

        context.setFillColor(color.withAlphaComponent(0.96).cgColor)
        context.fillEllipse(in: CGRect(x: point.x - 5, y: point.y - 5, width: 10, height: 10))

        context.restoreGState()
    }

    private static func draw(_ annotation: Annotation, in context: CGContext, crop: CGRect, height: Int) {
        let area = shift(annotation.Area.rect, crop, height)
        let color = NSColor.fromHex(annotation.Color)
        let thickness = CGFloat(max(2, annotation.Thickness))

        context.saveGState()
        defer { context.restoreGState() }

        switch annotation.Kind {
        case .rectangle:
            context.setStrokeColor(color.cgColor)
            context.setLineWidth(thickness)
            context.addPath(CGPath(roundedRect: area.standardized, cornerWidth: 6, cornerHeight: 6, transform: nil))
            context.strokePath()

        case .highlight:
            context.setFillColor(color.withAlphaComponent(0.28).cgColor)
            context.fill(area.standardized)

        case .arrow:
            drawArrow(context, from: CGPoint(x: area.minX, y: area.maxY), to: CGPoint(x: area.maxX, y: area.minY), color: color, width: thickness + 1)

        case .text:
            drawLabel(context, annotation.Text, at: CGPoint(x: area.minX, y: area.maxY), color: color, height: height)

        default:
            break
        }
    }

    private static func drawArrow(
        _ context: CGContext,
        from: CGPoint,
        to: CGPoint,
        color: NSColor,
        width: CGFloat) {
        context.setStrokeColor(color.cgColor)
        context.setFillColor(color.cgColor)
        context.setLineWidth(width)
        context.setLineCap(.round)

        context.move(to: from)
        context.addLine(to: to)
        context.strokePath()

        let angle = atan2(to.y - from.y, to.x - from.x)
        let head = width * 4.5

        context.move(to: to)
        context.addLine(to: CGPoint(
            x: to.x - head * cos(angle - .pi / 7),
            y: to.y - head * sin(angle - .pi / 7)))
        context.addLine(to: CGPoint(
            x: to.x - head * cos(angle + .pi / 7),
            y: to.y - head * sin(angle + .pi / 7)))
        context.closePath()
        context.fillPath()
    }

    private static func drawLabel(
        _ context: CGContext,
        _ text: String,
        at point: CGPoint,
        color: NSColor,
        height: Int) {
        guard !text.isEmpty else { return }

        let font = NSFont.systemFont(ofSize: 20, weight: .semibold)
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font,
            .foregroundColor: NSColor.white,
        ]

        let string = NSAttributedString(string: text, attributes: attributes)
        let measured = string.size()
        let pill = CGRect(
            x: point.x,
            y: point.y - measured.height - 12,
            width: measured.width + 24,
            height: measured.height + 14)

        context.setFillColor(color.cgColor)
        context.addPath(CGPath(roundedRect: pill, cornerWidth: 8, cornerHeight: 8, transform: nil))
        context.fillPath()

        let previous = NSGraphicsContext.current
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: false)
        string.draw(at: CGPoint(x: pill.minX + 12, y: pill.minY + 7))
        NSGraphicsContext.current = previous
    }

    /// Blocks out a region, reading from the untouched screenshot rather than from the
    /// surface being drawn on.
    private static func pixelate(
        _ context: CGContext,
        source: CGImage,
        area: CGRect,
        crop: CGRect,
        height: Int) {
        let region = area.standardized.intersection(CGRect(
            x: crop.origin.x,
            y: crop.origin.y,
            width: crop.width,
            height: crop.height))

        if region.width < 2 || region.height < 2 { return }
        guard let piece = source.cropping(to: region) else { return }

        let blocks = max(4, min(24, Int(region.width) / 14))
        let smallHeight = max(1, Int(region.height) * blocks / max(1, Int(region.width)))

        guard let small = CGContext(
            data: nil,
            width: blocks,
            height: smallHeight,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }

        small.interpolationQuality = .medium
        small.draw(piece, in: CGRect(x: 0, y: 0, width: blocks, height: smallHeight))

        guard let blurred = small.makeImage() else { return }

        context.saveGState()
        context.interpolationQuality = .none
        context.draw(blurred, in: shift(region, crop, height))
        context.restoreGState()
    }

    // ------------------------------------------------------------------ helpers

    static func resize(_ image: CGImage, maxWidth: Int) -> CGImage {
        if image.width <= maxWidth { return image }

        let scale = Double(maxWidth) / Double(image.width)
        let width = maxWidth
        let height = max(1, Int(Double(image.height) * scale))

        guard let context = CGContext(
            data: nil,
            width: width,
            height: height,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return image }

        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage() ?? image
    }

    /// The finished picture for a step, ready for the editor or an export.
    static func render(
        guide: Guide,
        step: Step,
        settings: Settings,
        maxWidth: Int = 1600) -> CGImage? {
        guard let path = guide.imagePath(step), let source = ImageFile.load(path) else { return nil }
        guard let composed = compose(
            step: step,
            source: source,
            markerHex: settings.markerColor,
            padding: settings.zoomPadding) else { return nil }

        return resize(composed, maxWidth: maxWidth)
    }
}
