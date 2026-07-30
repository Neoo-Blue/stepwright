import AppKit
import CoreGraphics
import Foundation

enum RecorderState {
    case idle
    case recording
    case paused
}

/// Watches the whole desktop and turns what the person does into finished steps.
///
/// The event callback does one thing only: take a single screen grab, which has to happen
/// before the clicked application redraws itself. Everything slow, the accessibility lookup
/// and writing the picture out, happens on the worker queue behind it. When a click ends a
/// burst of typing, both steps share that one grab.
final class Recorder {
    private enum Work {
        case click(CapturedFrame, CGPoint, StepKind, Bool)
        case type(CapturedFrame, String, ElementInfo?)
        case keys(CapturedFrame, CGPoint, String)
        case scroll(CapturedFrame, CGPoint, String)
        case shot(CapturedFrame, CGPoint)
        case promote(StepKind, String)
    }

    private let settings: Settings
    private let queue = DispatchQueue(label: "stepwright.recorder", qos: .userInitiated)

    private var tap: CFMachPort?
    private var runLoopSource: CFRunLoopSource?
    private var mediaFolder: URL?
    private var sessionStamp = ""
    private var imageCounter = 0

    // Gesture state, touched only on the main thread where the tap callback runs.
    private var downPoint: CGPoint = .zero
    private var lastLeftDownAt: TimeInterval = 0
    private var lastLeftDownPoint: CGPoint = .zero
    private var downWasRecorded = false
    private var lastScrollAt: TimeInterval = 0
    private var lastScrollSign: Int = 0
    private var lastScrollHorizontal = false
    private var lastCommandKey = ""
    private var lastCommandAt: TimeInterval = 0

    private var typing = ""
    private var typingTarget: ElementInfo?
    private var typingTimer: Timer?

    // Worker state.
    private var lastStep: Step?
    private var lastElement: ElementInfo?
    private var lastClickScreenPoint: CGPoint = .zero
    private var previousApp = ""

    private var startedAt = Date()
    private var accumulated: TimeInterval = 0

    private(set) var state: RecorderState = .idle
    private(set) var stepCount = 0

    /// Raised on the main thread once a step is finished.
    var onStepAdded: ((Step) -> Void)?
    var onStepChanged: ((Step) -> Void)?
    var onStateChanged: (() -> Void)?

    init(settings: Settings) {
        self.settings = settings
    }

    var elapsed: TimeInterval {
        state == .recording ? accumulated + Date().timeIntervalSince(startedAt) : accumulated
    }

    // ------------------------------------------------------------------ lifetime

    @discardableResult
    func start(mediaFolder folder: URL) -> String? {
        guard state == .idle else { return nil }

        mediaFolder = folder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        let stamp = DateFormatter()
        stamp.dateFormat = "yyyyMMddHHmmss"
        sessionStamp = stamp.string(from: Date())

        imageCounter = 0
        stepCount = 0
        lastStep = nil
        lastElement = nil
        previousApp = ""
        resetGestureState()
        Inspector.resetCache()

        guard let tap = makeTap() else {
            return "macOS refused the event tap. Grant Accessibility, then try again."
        }

        self.tap = tap
        runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)

        accumulated = 0
        startedAt = Date()
        state = .recording
        onStateChanged?()
        return nil
    }

    func pause() {
        guard state == .recording else { return }
        flushTyping(sharing: nil)

        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        accumulated += Date().timeIntervalSince(startedAt)
        state = .paused
        onStateChanged?()
    }

    func resume() {
        guard state == .paused else { return }
        resetGestureState()

        if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
        startedAt = Date()
        state = .recording
        onStateChanged?()
    }

    func stop() {
        guard state != .idle else { return }
        flushTyping(sharing: nil)

        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }

        if let runLoopSource {
            CFRunLoopRemoveSource(CFRunLoopGetMain(), runLoopSource, .commonModes)
        }

        if let tap { CFMachPortInvalidate(tap) }

        tap = nil
        runLoopSource = nil

        if state == .recording {
            accumulated += Date().timeIntervalSince(startedAt)
        }

        state = .idle

        // Let anything already captured finish before the editor settles.
        queue.sync {}
        onStateChanged?()
    }

    /// Captures the screen as a step of its own, with no click marker.
    func captureManualShot() {
        guard state == .recording else { return }

        let cursor = ScreenCapture.cursorPosition()
        guard let frame = ScreenCapture.grab(at: cursor, allDisplays: settings.captureAllDisplays) else { return }

        flushTyping(sharing: frame)
        enqueue(.shot(frame, cursor))
    }

    private func resetGestureState() {
        lastLeftDownAt = 0
        lastLeftDownPoint = .zero
        lastScrollAt = 0
        lastScrollSign = 0
        lastScrollHorizontal = false
        lastCommandKey = ""
        lastCommandAt = 0
        downWasRecorded = false
        typing = ""
        typingTarget = nil
        typingTimer?.invalidate()
        typingTimer = nil
    }

    // ------------------------------------------------------------------ the tap

    private func makeTap() -> CFMachPort? {
        let mask: CGEventMask =
            (1 << CGEventType.leftMouseDown.rawValue) |
            (1 << CGEventType.leftMouseUp.rawValue) |
            (1 << CGEventType.rightMouseDown.rawValue) |
            (1 << CGEventType.otherMouseDown.rawValue) |
            (1 << CGEventType.scrollWheel.rawValue) |
            (1 << CGEventType.keyDown.rawValue)

        let context = Unmanaged.passUnretained(self).toOpaque()

        return CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, pointer in
                guard let pointer else { return Unmanaged.passUnretained(event) }
                let recorder = Unmanaged<Recorder>.fromOpaque(pointer).takeUnretainedValue()
                recorder.handle(type: type, event: event)
                return Unmanaged.passUnretained(event)
            },
            userInfo: context)
    }

    private func handle(type: CGEventType, event: CGEvent) {
        // macOS switches a tap off if it ever takes too long. It says so, which means it can
        // simply be switched back on rather than silently recording nothing.
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            return
        }

        guard state == .recording else { return }

        switch type {
        case .leftMouseDown:
            mouseDown(event.location, kind: .click)
        case .rightMouseDown:
            mouseDown(event.location, kind: .rightClick)
        case .otherMouseDown:
            mouseDown(event.location, kind: .middleClick)
        case .leftMouseUp:
            mouseUp(event.location)
        case .scrollWheel:
            scroll(event)
        case .keyDown:
            key(event)
        default:
            break
        }
    }

    // ------------------------------------------------------------------ gestures

    private func mouseDown(_ point: CGPoint, kind: StepKind) {
        downPoint = point
        downWasRecorded = false

        if belongsToThisApp(point) { return }

        let now = ProcessInfo.processInfo.systemUptime

        let repeatOfLeft = kind == .click
            && lastLeftDownAt > 0
            && now - lastLeftDownAt <= NSEvent.doubleClickInterval
            && abs(point.x - lastLeftDownPoint.x) <= 6
            && abs(point.y - lastLeftDownPoint.y) <= 6

        if kind == .click {
            lastLeftDownAt = now
            lastLeftDownPoint = point
        }

        guard let frame = ScreenCapture.grab(at: point, allDisplays: settings.captureAllDisplays) else { return }

        flushTyping(sharing: frame)
        enqueue(.click(frame, point, kind, repeatOfLeft))
        downWasRecorded = true
    }

    private func mouseUp(_ point: CGPoint) {
        guard settings.captureDrag, downWasRecorded else { return }

        let dx = point.x - downPoint.x
        let dy = point.y - downPoint.y
        if abs(dx) < 14 && abs(dy) < 14 { return }

        let direction = abs(dx) > abs(dy)
            ? (dx > 0 ? "to the right" : "to the left")
            : (dy > 0 ? "downwards" : "upwards")

        enqueue(.promote(.drag, direction))
    }

    private func scroll(_ event: CGEvent) {
        guard settings.captureScroll else { return }

        let point = event.location
        if belongsToThisApp(point) { return }

        let vertical = event.getDoubleValueField(.scrollWheelEventDeltaAxis1)
        let horizontal = event.getDoubleValueField(.scrollWheelEventDeltaAxis2)
        let isHorizontal = abs(horizontal) > abs(vertical)
        let delta = isHorizontal ? horizontal : vertical

        if delta == 0 { return }

        let now = ProcessInfo.processInfo.systemUptime
        let sign = delta < 0 ? -1 : 1

        let sameBurst = now - lastScrollAt < 0.9
            && sign == lastScrollSign
            && isHorizontal == lastScrollHorizontal

        lastScrollAt = now
        lastScrollSign = sign
        lastScrollHorizontal = isHorizontal

        if sameBurst { return }

        guard let frame = ScreenCapture.grab(at: point, allDisplays: settings.captureAllDisplays) else { return }

        flushTyping(sharing: frame)
        enqueue(.scroll(frame, point, StepText.scrollDirection(delta, horizontal: isHorizontal)))
    }

    private func key(_ event: CGEvent) {
        guard settings.captureKeyboard else { return }
        if isFrontmostOurs() { return }

        let code = CGKeyCode(event.getIntegerValueField(.keyboardEventKeycode))
        let flags = event.flags

        let command = flags.contains(.maskCommand)
        let control = flags.contains(.maskControl)
        let option = flags.contains(.maskAlternate)
        let shift = flags.contains(.maskShift)

        // Command or Control turns a key into an instruction rather than text.
        if command || control {
            let name = KeyNames.name(for: code) ?? characters(event).uppercased()
            if name.isEmpty { return }

            recordCommandKey(KeyNames.combination(
                command: command,
                option: option,
                control: control,
                shift: shift,
                key: name))
            return
        }

        if let named = KeyNames.name(for: code), KeyNames.notableCodes.contains(code) {
            if code == 51, !typing.isEmpty {
                // Delete inside a burst simply corrects what was typed.
                typing.removeLast()
                restartTypingTimer()
                return
            }

            let label = shift
                ? KeyNames.combination(command: false, option: false, control: false, shift: true, key: named)
                : named

            recordCommandKey(label)
            return
        }

        let text = characters(event)
        if !text.isEmpty {
            appendTyping(text)
        }
    }

    private func characters(_ event: CGEvent) -> String {
        var length = 0
        var buffer = [UniChar](repeating: 0, count: 8)
        event.keyboardGetUnicodeString(maxStringLength: 8, actualStringLength: &length, unicodeString: &buffer)

        if length == 0 { return "" }

        let text = String(utf16CodeUnits: buffer, count: length)
        return text.unicodeScalars.allSatisfy { !CharacterSet.controlCharacters.contains($0) } ? text : ""
    }

    /// Records a named key or a shortcut, ignoring the stream of repeats while a key is held.
    private func recordCommandKey(_ keys: String) {
        let now = ProcessInfo.processInfo.systemUptime
        if keys == lastCommandKey, now - lastCommandAt < 0.35 {
            lastCommandAt = now
            return
        }

        lastCommandKey = keys
        lastCommandAt = now

        let cursor = ScreenCapture.cursorPosition()
        guard let frame = ScreenCapture.grab(at: cursor, allDisplays: settings.captureAllDisplays) else { return }

        flushTyping(sharing: frame)
        enqueue(.keys(frame, cursor, keys))
    }

    private func appendTyping(_ text: String) {
        if typing.isEmpty {
            typingTarget = nil

            // Work out what is being typed into while the burst is still fresh.
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                let focused = Inspector.focused()
                DispatchQueue.main.async { self?.typingTarget = focused }
            }
        }

        typing += text
        restartTypingTimer()

        if typing.count >= 120 { flushTyping(sharing: nil) }
    }

    private func restartTypingTimer() {
        typingTimer?.invalidate()
        typingTimer = Timer.scheduledTimer(
            withTimeInterval: max(0.4, settings.typingMergeSeconds),
            repeats: false) { [weak self] _ in
            self?.flushTyping(sharing: nil)
        }
    }

    /// Turns the buffered keystrokes into a step, sharing a grab when one is going anyway.
    private func flushTyping(sharing frame: CapturedFrame?) {
        typingTimer?.invalidate()
        typingTimer = nil

        guard !typing.isEmpty else { return }

        let text = typing
        typing = ""

        let target = typingTarget
        typingTarget = nil

        let picture: CapturedFrame?
        if let frame {
            picture = frame
        } else {
            picture = ScreenCapture.grab(at: ScreenCapture.cursorPosition(), allDisplays: settings.captureAllDisplays)
        }

        guard let picture else { return }
        enqueue(.type(picture, text, target))
    }

    private func belongsToThisApp(_ point: CGPoint) -> Bool {
        // Our own floating bar is excluded from capture and from the guide.
        for window in NSApp.windows where window.isVisible {
            var frame = window.frame
            // Window frames are measured from the bottom of the main display upwards.
            if let screen = NSScreen.screens.first {
                frame.origin.y = screen.frame.height - frame.origin.y - frame.height
            }

            if frame.insetBy(dx: -2, dy: -2).contains(point) { return true }
        }

        return false
    }

    private func isFrontmostOurs() -> Bool {
        NSRunningApplication.current.isActive
    }

    // ------------------------------------------------------------------ the worker

    private func enqueue(_ work: Work) {
        queue.async { [weak self] in self?.process(work) }
    }

    private func process(_ work: Work) {
        switch work {
        case let .click(frame, point, kind, maybeSecond):
            handleClick(frame, point, kind, maybeSecond)
        case let .type(frame, text, target):
            handleType(frame, text, target)
        case let .keys(frame, point, keys):
            handleKeys(frame, point, keys)
        case let .scroll(frame, point, direction):
            handleScroll(frame, point, direction)
        case let .shot(frame, point):
            handleShot(frame, point)
        case let .promote(kind, detail):
            handlePromote(kind, detail)
        }
    }

    private func saveFrame(_ frame: CapturedFrame) -> String {
        guard let folder = mediaFolder else { return "" }
        imageCounter += 1
        let index = imageCounter
        return frame.saveOnce(in: folder, name: String(format: "%@_%04d.png", sessionStamp, index))
    }

    private func appPlace(_ element: ElementInfo) -> String {
        let app = StepText.appContext(element)
        if app.isEmpty || app.caseInsensitiveCompare(previousApp) == .orderedSame { return "" }
        previousApp = app
        return app
    }

    private func handleClick(_ frame: CapturedFrame, _ point: CGPoint, _ kind: StepKind, _ maybeSecond: Bool) {
        if maybeSecond, promoteToDoubleClick(at: point) { return }

        let element = Inspector.resolve(at: point)
        let place = appPlace(element)

        let step = Step()
        step.Kind = kind
        step.ClickPoint = PointI(frame.toImagePoint(point))
        step.ShowClickMarker = settings.showClickMarker
        step.ShowElementOutline = settings.showElementOutline
        step.AutoZoom = settings.autoZoom
        step.AppName = element.appName
        step.WindowTitle = element.windowTitle
        step.ElementName = element.name
        step.ElementType = element.controlType

        if !element.bounds.isEmpty {
            step.ElementArea = RectI(frame.toImageRect(element.bounds))
        }

        if !element.windowBounds.isEmpty {
            step.WindowArea = RectI(frame.toImageRect(element.windowBounds))
        }

        step.Text = StepText.describe(kind: kind, element: element, place: place)
        step.OriginalText = step.Text
        step.Image = saveFrame(frame)

        lastClickScreenPoint = point
        publish(step, element)
    }

    /// Folds a rapid second click into the step the first one produced, unless the previous
    /// step has moved on, in which case a normal step is recorded instead of losing the click.
    private func promoteToDoubleClick(at point: CGPoint) -> Bool {
        guard let previous = lastStep, previous.Kind == .click, let element = lastElement else { return false }

        if abs(point.x - lastClickScreenPoint.x) > 6 || abs(point.y - lastClickScreenPoint.y) > 6 {
            return false
        }

        let edited = previous.Text != previous.OriginalText
        previous.Kind = .doubleClick

        if !edited {
            previous.Text = StepText.describe(kind: .doubleClick, element: element, place: "")
            previous.OriginalText = previous.Text
        }

        DispatchQueue.main.async { [weak self] in self?.onStepChanged?(previous) }
        return true
    }

    private func handleType(_ frame: CapturedFrame, _ text: String, _ target: ElementInfo?) {
        let element = target ?? ElementInfo()
        let place = appPlace(element)

        // Fail closed. Without knowing the target there is no way to know it was not a
        // password box, so the characters are not written down.
        let unknown = target == nil
        let secret = settings.redactPasswords && (element.isPassword || unknown)
        let shown = secret ? "" : settings.redact(text)

        let step = Step()
        step.Kind = .type
        step.ShowClickMarker = false
        step.ShowElementOutline = settings.showElementOutline
        step.AutoZoom = settings.autoZoom
        step.AppName = element.appName
        step.WindowTitle = element.windowTitle
        step.ElementName = element.name
        step.ElementType = element.controlType
        step.TypedText = shown
        step.Redacted = secret

        if !element.bounds.isEmpty {
            step.ElementArea = RectI(frame.toImageRect(element.bounds))
        }

        if !element.windowBounds.isEmpty {
            step.WindowArea = RectI(frame.toImageRect(element.windowBounds))
        }

        if secret {
            let suffix = place.isEmpty ? "" : " in " + place
            step.Text = element.isPassword
                ? StepText.describeRedactedTyping(element: element, inApp: suffix)
                : "Enter your details" + suffix + "."
        } else {
            step.Text = StepText.describe(kind: .type, element: element, place: place, extra: shown)
        }

        step.OriginalText = step.Text
        step.Image = saveFrame(frame)
        publish(step, element)
    }

    private func handleKeys(_ frame: CapturedFrame, _ point: CGPoint, _ keys: String) {
        let element = Inspector.resolve(at: point, timeout: 0.4)
        let place = appPlace(element)

        let step = Step()
        step.Kind = .hotkey
        step.ShowClickMarker = false
        step.ShowElementOutline = false

        // A shortcut has no click to point at, but it does have a window, and a picture of the
        // window beats a picture of the whole desktop with the work lost in the middle.
        step.AutoZoom = settings.autoZoom
        step.Keys = keys
        step.AppName = element.appName
        step.WindowTitle = element.windowTitle

        if !element.windowBounds.isEmpty {
            step.WindowArea = RectI(frame.toImageRect(element.windowBounds))
        }

        step.Text = StepText.describe(kind: .hotkey, element: element, place: place, extra: keys)
        step.OriginalText = step.Text
        step.Image = saveFrame(frame)

        publish(step, element)
    }

    private func handleScroll(_ frame: CapturedFrame, _ point: CGPoint, _ direction: String) {
        let element = Inspector.resolve(at: point, timeout: 0.4)
        let place = appPlace(element)

        let step = Step()
        step.Kind = .scroll
        step.ShowClickMarker = false
        step.ShowElementOutline = false
        step.AutoZoom = settings.autoZoom
        step.AppName = element.appName
        step.WindowTitle = element.windowTitle

        if !element.windowBounds.isEmpty {
            step.WindowArea = RectI(frame.toImageRect(element.windowBounds))
        }

        step.Text = StepText.describe(kind: .scroll, element: element, place: place, extra: direction)
        step.OriginalText = step.Text
        step.Image = saveFrame(frame)

        publish(step, element)
    }

    private func handleShot(_ frame: CapturedFrame, _ point: CGPoint) {
        let element = Inspector.resolve(at: point, timeout: 0.4)

        let step = Step()
        step.Kind = .screenshot
        step.ShowClickMarker = false
        step.ShowElementOutline = false
        step.AutoZoom = false
        step.AppName = element.appName
        step.WindowTitle = element.windowTitle
        step.Text = StepText.describe(kind: .screenshot, element: element, place: "")
        step.OriginalText = step.Text
        step.Image = saveFrame(frame)

        publish(step, element)
    }

    private func handlePromote(_ kind: StepKind, _ detail: String) {
        guard let step = lastStep, let element = lastElement else { return }
        if kind == .drag, step.Kind != .click, step.Kind != .doubleClick { return }

        let edited = step.Text != step.OriginalText
        step.Kind = kind

        if !edited {
            step.Text = StepText.describe(kind: kind, element: element, place: "", extra: detail)
            step.OriginalText = step.Text
        }

        DispatchQueue.main.async { [weak self] in self?.onStepChanged?(step) }
    }

    private func publish(_ step: Step, _ element: ElementInfo) {
        lastStep = step
        lastElement = element
        stepCount += 1
        DispatchQueue.main.async { [weak self] in self?.onStepAdded?(step) }
    }
}
