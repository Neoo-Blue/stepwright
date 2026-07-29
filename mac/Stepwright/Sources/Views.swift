import AppKit
import Foundation

/// Shows the composed picture for a step and lets the person draw on it.
///
/// Everything reported back is in the coordinates of the original screenshot, so a later
/// change of framing keeps the callouts where they belong.
final class PreviewView: NSView {
    enum Tool {
        case select
        case box
        case arrow
        case blur
        case highlight
        case label
        case crop
        case marker
    }

    private var image: CGImage?
    private var origin: CGPoint = .zero
    private var target: CGRect = .zero
    private var scale: CGFloat = 1
    private var dragging = false
    private var dragStart: CGPoint = .zero
    private var dragNow: CGPoint = .zero

    var tool: Tool = .select
    var drawColor: NSColor = .systemRed
    var emptyMessage = "Nothing recorded yet"
    var emptyHint = ""

    /// Reports a dragged region, in the coordinates of the original screenshot.
    var onRegion: ((CGRect, Tool) -> Void)?
    var onPoint: ((CGPoint, Tool) -> Void)?

    override var isFlipped: Bool { true }

    func show(_ picture: CGImage?, origin: CGPoint) {
        image = picture
        self.origin = origin
        dragging = false
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }

        Theme.background.setFill()
        bounds.fill()

        guard let image else {
            drawEmptyState()
            return
        }

        measure()

        context.saveGState()

        // Drawn through a flipped view, so the picture is turned the right way up.
        context.translateBy(x: 0, y: bounds.height)
        context.scaleBy(x: 1, y: -1)

        let flipped = CGRect(
            x: target.origin.x,
            y: bounds.height - target.origin.y - target.height,
            width: target.width,
            height: target.height)

        context.setShadow(offset: CGSize(width: 0, height: -3), blur: 14, color: NSColor(white: 0, alpha: 0.35).cgColor)
        context.interpolationQuality = .high
        context.draw(image, in: flipped)
        context.restoreGState()

        Theme.border.setStroke()
        let frame = NSBezierPath(roundedRect: target, xRadius: 8, yRadius: 8)
        frame.lineWidth = 1
        frame.stroke()

        if dragging { drawLivePreview() }
    }

    private func drawEmptyState() {
        let centre = CGPoint(x: bounds.midX, y: bounds.midY - 30)
        let plate = CGRect(x: centre.x - 46, y: centre.y - 46, width: 92, height: 92)

        Theme.panel.setFill()
        NSBezierPath(roundedRect: plate, xRadius: 22, yRadius: 22).fill()

        let glass = CGRect(x: plate.minX + 22, y: plate.minY + 26, width: 48, height: 36)
        Theme.raised.setFill()
        NSBezierPath(roundedRect: glass, xRadius: 5, yRadius: 5).fill()

        Theme.accent.setFill()
        NSBezierPath(ovalIn: CGRect(x: glass.maxX - 16, y: glass.maxY - 15, width: 9, height: 9)).fill()

        let title = NSAttributedString(string: emptyMessage, attributes: [
            .font: NSFont.systemFont(ofSize: 15, weight: .semibold),
            .foregroundColor: Theme.text,
        ])

        let titleSize = title.size()
        title.draw(at: CGPoint(x: bounds.midX - titleSize.width / 2, y: plate.maxY + 18))

        guard !emptyHint.isEmpty else { return }

        let hint = NSAttributedString(string: emptyHint, attributes: [
            .font: NSFont.systemFont(ofSize: 12),
            .foregroundColor: Theme.muted,
        ])

        let hintSize = hint.size()
        hint.draw(at: CGPoint(x: bounds.midX - hintSize.width / 2, y: plate.maxY + 44))
    }

    private func drawLivePreview() {
        let rect = normalized(dragStart, dragNow)

        switch tool {
        case .crop:
            NSColor(white: 0, alpha: 0.45).setFill()
            let area = NSBezierPath(rect: target)
            area.append(NSBezierPath(rect: rect).reversed)
            area.fill()

            NSColor.white.setStroke()
            let outline = NSBezierPath(rect: rect)
            outline.setLineDash([5, 3], count: 2, phase: 0)
            outline.stroke()

        case .blur:
            NSColor(white: 0.2, alpha: 0.5).setFill()
            NSBezierPath(rect: rect).fill()

        case .highlight:
            drawColor.withAlphaComponent(0.3).setFill()
            NSBezierPath(rect: rect).fill()

        case .arrow:
            drawColor.setStroke()
            let line = NSBezierPath()
            line.move(to: dragStart)
            line.line(to: dragNow)
            line.lineWidth = 4
            line.lineCapStyle = .round
            line.stroke()

        default:
            drawColor.setStroke()
            let outline = NSBezierPath(rect: rect)
            outline.lineWidth = 3
            outline.stroke()
        }
    }

    private func measure() {
        guard let image else { return }

        let margin: CGFloat = 16
        let available = max(1, bounds.width - (margin * 2))
        let availableHeight = max(1, bounds.height - (margin * 2))

        scale = min(available / CGFloat(image.width), availableHeight / CGFloat(image.height))
        scale = min(scale, 1)

        let width = max(1, CGFloat(image.width) * scale)
        let height = max(1, CGFloat(image.height) * scale)

        target = CGRect(
            x: (bounds.width - width) / 2,
            y: (bounds.height - height) / 2,
            width: width,
            height: height)
    }

    private func normalized(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }

    /// Maps a point in the view onto a pixel of the original screenshot.
    private func toSource(_ point: CGPoint) -> CGPoint {
        guard scale > 0 else { return .zero }
        return CGPoint(
            x: ((point.x - target.minX) / scale) + origin.x,
            y: ((point.y - target.minY) / scale) + origin.y)
    }

    override func mouseDown(with event: NSEvent) {
        measure()
        let point = convert(event.locationInWindow, from: nil)
        guard image != nil, target.contains(point) else { return }

        if tool == .select || tool == .marker || tool == .label {
            onPoint?(toSource(point), tool)
            if tool != .select { return }
        }

        if tool == .select { return }

        dragging = true
        dragStart = point
        dragNow = point
    }

    override func mouseDragged(with event: NSEvent) {
        guard dragging else { return }
        let point = convert(event.locationInWindow, from: nil)

        dragNow = CGPoint(
            x: min(max(point.x, target.minX), target.maxX),
            y: min(max(point.y, target.minY), target.maxY))

        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        guard dragging else { return }
        dragging = false
        needsDisplay = true

        let start = toSource(dragStart)
        let end = toSource(dragNow)

        let region = tool == .arrow
            ? CGRect(x: start.x, y: start.y, width: end.x - start.x, height: end.y - start.y)
            : CGRect(
                x: min(start.x, end.x),
                y: min(start.y, end.y),
                width: abs(end.x - start.x),
                height: abs(end.y - start.y))

        if tool != .arrow, region.width < 6 || region.height < 6 { return }
        onRegion?(region, tool)
    }
}

/// One row in the step list: a rounded thumbnail, the number on a badge, the sentence, and a
/// quiet line saying what kind of action it was.
final class StepRowView: NSView {
    private let thumbnail = NSImageView()
    private let badge = NSTextField(labelWithString: "")
    private let sentence = NSTextField(wrappingLabelWithString: "")
    private let trailer = NSTextField(labelWithString: "")

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        build()
    }

    required init?(coder: NSCoder) {
        super.init(coder: coder)
        build()
    }

    private func build() {
        wantsLayer = true

        thumbnail.imageScaling = .scaleProportionallyUpOrDown
        thumbnail.wantsLayer = true
        thumbnail.layer?.cornerRadius = 4
        thumbnail.layer?.masksToBounds = true
        thumbnail.layer?.backgroundColor = Theme.raised.cgColor

        badge.alignment = .center
        badge.font = .systemFont(ofSize: 10, weight: .bold)
        badge.textColor = .white
        badge.wantsLayer = true
        badge.layer?.cornerRadius = 10
        badge.layer?.masksToBounds = true
        badge.layer?.backgroundColor = Theme.accent.cgColor

        sentence.font = .systemFont(ofSize: 12.5)
        sentence.textColor = Theme.text
        sentence.maximumNumberOfLines = 3
        sentence.lineBreakMode = .byTruncatingTail

        trailer.font = .systemFont(ofSize: 10)
        trailer.textColor = Theme.muted

        for view in [thumbnail, badge, sentence, trailer] {
            view.translatesAutoresizingMaskIntoConstraints = false
            addSubview(view)
        }

        NSLayoutConstraint.activate([
            thumbnail.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 12),
            thumbnail.centerYAnchor.constraint(equalTo: centerYAnchor),
            thumbnail.widthAnchor.constraint(equalToConstant: 84),
            thumbnail.heightAnchor.constraint(equalToConstant: 52),

            badge.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 6),
            badge.topAnchor.constraint(equalTo: thumbnail.topAnchor, constant: -6),
            badge.widthAnchor.constraint(equalToConstant: 20),
            badge.heightAnchor.constraint(equalToConstant: 20),

            sentence.leadingAnchor.constraint(equalTo: thumbnail.trailingAnchor, constant: 12),
            sentence.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -10),
            sentence.topAnchor.constraint(equalTo: topAnchor, constant: 10),

            trailer.leadingAnchor.constraint(equalTo: sentence.leadingAnchor),
            trailer.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -8),
        ])
    }

    func show(step: Step, number: Int, picture: NSImage?, heading: Bool) {
        if heading {
            thumbnail.isHidden = true
            badge.isHidden = true
            sentence.font = .systemFont(ofSize: 14, weight: .bold)
            sentence.stringValue = step.Text.isEmpty ? "Section" : step.Text
            trailer.stringValue = "Section"
            return
        }

        thumbnail.isHidden = false
        badge.isHidden = false
        sentence.font = .systemFont(ofSize: 12.5)
        sentence.stringValue = step.Text
        thumbnail.image = picture
        badge.stringValue = "\(number)"
        badge.layer?.backgroundColor = (step.Skip ? Theme.border : Theme.accent).cgColor

        var trailing = Theme.describe(step.Kind)
        if step.Skip { trailing = "hidden" } else if step.Animate { trailing += "   animated" }
        trailer.stringValue = trailing

        sentence.textColor = step.Skip ? Theme.muted : Theme.text
    }
}

/// The small floating bar that stays on top while a recording runs. macOS is asked to leave
/// it out of any screen capture, so it never appears in the guide.
final class RecorderBar: NSPanel {
    private let status = NSTextField(labelWithString: "00:00   0 steps")
    private let pauseButton = NSButton(title: "Pause", target: nil, action: nil)
    private var timer: Timer?

    var elapsedSource: (() -> TimeInterval)?
    var stepCountSource: (() -> Int)?
    var onPause: (() -> Void)?
    var onStop: (() -> Void)?
    var onShot: (() -> Void)?

    var paused = false {
        didSet { pauseButton.title = paused ? "Resume" : "Pause" }
    }

    init() {
        super.init(
            contentRect: NSRect(x: 0, y: 0, width: 430, height: 56),
            styleMask: [.borderless, .nonactivatingPanel],
            backing: .buffered,
            defer: false)

        isFloatingPanel = true
        level = .statusBar
        backgroundColor = .clear
        isOpaque = false
        hasShadow = true
        hidesOnDeactivate = false
        isMovableByWindowBackground = true

        // The whole point of the bar is that it does not end up in the screenshots.
        sharingType = .none
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]

        let container = NSView()
        container.wantsLayer = true
        container.layer?.backgroundColor = NSColor(srgbRed: 0.08, green: 0.08, blue: 0.1, alpha: 0.97).cgColor
        container.layer?.cornerRadius = 14
        container.layer?.borderWidth = 1
        container.layer?.borderColor = NSColor(white: 0.28, alpha: 1).cgColor

        let dot = NSView()
        dot.wantsLayer = true
        dot.layer?.backgroundColor = NSColor.systemRed.cgColor
        dot.layer?.cornerRadius = 5

        status.textColor = .white
        status.font = .systemFont(ofSize: 13)

        let shot = NSButton(title: "Capture", target: self, action: #selector(shotTapped))
        pauseButton.target = self
        pauseButton.action = #selector(pauseTapped)

        let finish = NSButton(title: "Finish", target: self, action: #selector(stopTapped))
        finish.bezelColor = .systemRed

        for button in [shot, pauseButton, finish] {
            button.bezelStyle = .rounded
            button.controlSize = .regular
        }

        let stack = NSStackView(views: [dot, status, shot, pauseButton, finish])
        stack.orientation = .horizontal
        stack.spacing = 10
        stack.alignment = .centerY
        stack.edgeInsets = NSEdgeInsets(top: 0, left: 16, bottom: 0, right: 14)
        stack.translatesAutoresizingMaskIntoConstraints = false

        container.addSubview(stack)
        contentView = container

        NSLayoutConstraint.activate([
            dot.widthAnchor.constraint(equalToConstant: 10),
            dot.heightAnchor.constraint(equalToConstant: 10),
            status.widthAnchor.constraint(greaterThanOrEqualToConstant: 120),
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            stack.centerYAnchor.constraint(equalTo: container.centerYAnchor),
        ])

        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            self?.refresh()
        }
    }

    override var canBecomeKey: Bool { false }

    func placeBottomCentre() {
        guard let screen = NSScreen.main else { return }
        let area = screen.visibleFrame
        setFrameOrigin(NSPoint(
            x: area.midX - (frame.width / 2),
            y: area.minY + 40))
    }

    func refresh() {
        let elapsed = Int(elapsedSource?() ?? 0)
        let steps = stepCountSource?() ?? 0
        let clock = String(format: "%02d:%02d", elapsed / 60, elapsed % 60)
        status.stringValue = paused
            ? "Paused   \(steps) \(steps == 1 ? "step" : "steps")"
            : "\(clock)   \(steps) \(steps == 1 ? "step" : "steps")"
    }

    func close(finished: Bool) {
        timer?.invalidate()
        timer = nil
        orderOut(nil)
    }

    @objc private func pauseTapped() { onPause?() }
    @objc private func stopTapped() { onStop?() }
    @objc private func shotTapped() { onShot?() }
}

enum Theme {
    static let background = NSColor(srgbRed: 0.08, green: 0.082, blue: 0.098, alpha: 1)
    static let panel = NSColor(srgbRed: 0.106, green: 0.114, blue: 0.133, alpha: 1)
    static let raised = NSColor(srgbRed: 0.141, green: 0.153, blue: 0.18, alpha: 1)
    static let border = NSColor(srgbRed: 0.188, green: 0.204, blue: 0.239, alpha: 1)
    static let text = NSColor(srgbRed: 0.925, green: 0.933, blue: 0.953, alpha: 1)
    static let muted = NSColor(srgbRed: 0.557, green: 0.584, blue: 0.631, alpha: 1)
    static let accent = NSColor(srgbRed: 0.345, green: 0.518, blue: 1, alpha: 1)

    static func describe(_ kind: StepKind) -> String {
        switch kind {
        case .click: return "Click"
        case .doubleClick: return "Double click"
        case .rightClick: return "Right click"
        case .middleClick: return "Middle click"
        case .drag: return "Drag"
        case .type: return "Typing"
        case .hotkey: return "Shortcut"
        case .scroll: return "Scroll"
        case .screenshot: return "Screen"
        case .note: return "Note"
        case .heading: return "Section"
        }
    }
}
