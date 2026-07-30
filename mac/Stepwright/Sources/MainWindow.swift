import AppKit
import Foundation

/// The editor: the guide on the left, the step being worked on to the right.
final class MainWindow: NSWindowController, NSTableViewDataSource, NSTableViewDelegate,
    NSTextViewDelegate, NSTextFieldDelegate {
    private let settings = Settings()
    private var recorder: Recorder!
    private var guide = Guide()

    private let table = NSTableView()
    private let titleField = NSTextField()
    private let summaryField = NSTextField()
    private let stepField = NSTextView()
    private let noteField = NSTextField()
    private let preview = PreviewView()
    private let statusLabel = NSTextField(labelWithString: "Ready.")
    private let countLabel = NSTextField(labelWithString: "")

    private var recordButton = NSButton()
    private var finishButton = NSButton()
    private var animateButton = NSButton()
    private var variantButtons: [NSButton] = []
    private var bar: RecorderBar?
    private var thumbnails: [String: NSImage] = [:]
    private var loading = false
    private var lastVariant: CropVariant = .focus
    private var globalKeys: Any?
    private var localKeys: Any?
    private var permissionsWindow: PermissionsWindow?

    convenience init() {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1280, height: 820),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)

        window.title = "Stepwright"
        window.titlebarAppearsTransparent = true
        window.minSize = NSSize(width: 1020, height: 640)
        window.center()

        self.init(window: window)
        window.appearance = NSAppearance(named: .darkAqua)

        recorder = Recorder(settings: settings)
        recorder.onStepAdded = { [weak self] step in self?.stepArrived(step) }
        recorder.onStepChanged = { [weak self] step in self?.stepRewritten(step) }

        build()
        newGuide(askToSave: false)
        watchForShortcuts()
        GuideStore.cleanupOldWorkFolders()

        // Better to say so at the start than to let a recording fail later.
        if !Permissions.allGranted || Permissions.needsMoving {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { [weak self] in
                self?.showPermissions()
                self?.updatePermissionNotice()
            }
        }
    }

    // ------------------------------------------------------------------ layout

    private func build() {
        guard let window, let content = window.contentView else { return }
        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.background.cgColor

        let toolbar = buildToolbar()
        let split = NSSplitView()
        split.isVertical = true
        split.dividerStyle = .thin
        split.addArrangedSubview(buildLibrary())
        split.addArrangedSubview(buildEditor())

        let status = buildStatusBar()

        let stack = NSStackView(views: [toolbar, split, status])
        stack.orientation = .vertical
        stack.spacing = 0
        stack.distribution = .fill
        stack.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            toolbar.heightAnchor.constraint(equalToConstant: 44),
            status.heightAnchor.constraint(equalToConstant: 26),
        ])

        DispatchQueue.main.async { split.setPosition(360, ofDividerAt: 0) }
    }

    private func buildToolbar() -> NSView {
        let row = NSStackView()
        row.orientation = .horizontal
        row.spacing = 6
        row.edgeInsets = NSEdgeInsets(top: 6, left: 12, bottom: 6, right: 12)
        row.wantsLayer = true
        row.layer?.backgroundColor = Theme.panel.cgColor

        row.addArrangedSubview(button("New", #selector(newTapped)))
        row.addArrangedSubview(button("Open", #selector(openTapped)))
        row.addArrangedSubview(button("Save", #selector(saveTapped)))

        recordButton = button("Record", #selector(recordTapped))
        recordButton.bezelColor = Theme.accent
        row.addArrangedSubview(recordButton)

        finishButton = button("Finish", #selector(finishTapped))
        finishButton.isEnabled = false
        row.addArrangedSubview(finishButton)

        row.addArrangedSubview(button("Add note", #selector(addNoteTapped)))
        row.addArrangedSubview(button("Add heading", #selector(addHeadingTapped)))
        row.addArrangedSubview(button("Improve with AI", #selector(improveTapped)))

        let exports = NSPopUpButton(title: "Export", target: nil, action: nil)
        exports.pullsDown = true
        exports.addItems(withTitles: [
            "Export",
            "Web page, everything in one file",
            "Web page with an images folder",
            "Markdown with an images folder",
            "PDF",
            "The whole guide as one animation",
            "Copy for pasting",
            "Copy as plain text",
        ])

        exports.target = self
        exports.action = #selector(exportChosen(_:))
        row.addArrangedSubview(exports)

        let publishing = NSPopUpButton(title: "Publish", target: nil, action: nil)
        publishing.pullsDown = true
        publishing.addItems(withTitles: ["Publish", "Send to Hudu", "Send to Confluence"])
        publishing.target = self
        publishing.action = #selector(publishChosen(_:))
        row.addArrangedSubview(publishing)

        row.addArrangedSubview(NSView())
        row.addArrangedSubview(button("Permissions", #selector(permissionsTapped)))
        row.addArrangedSubview(button("Settings", #selector(settingsTapped)))
        return row
    }

    private func buildLibrary() -> NSView {
        let panel = NSView()
        panel.wantsLayer = true
        panel.layer?.backgroundColor = Theme.panel.cgColor

        titleField.placeholderString = "Guide title"
        titleField.font = .systemFont(ofSize: 14, weight: .semibold)
        titleField.delegate = self

        summaryField.placeholderString = "Summary"
        summaryField.delegate = self

        table.headerView = nil
        table.rowHeight = 70
        table.backgroundColor = Theme.panel
        table.selectionHighlightStyle = .regular
        table.addTableColumn(NSTableColumn(identifier: NSUserInterfaceItemIdentifier("step")))
        table.dataSource = self
        table.delegate = self

        let scroll = NSScrollView()
        scroll.documentView = table
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false

        let tools = NSStackView(views: [
            button("Up", #selector(moveUpTapped)),
            button("Down", #selector(moveDownTapped)),
            button("Merge", #selector(mergeTapped)),
            button("Copy", #selector(duplicateTapped)),
            button("Hide", #selector(hideTapped)),
            button("Delete", #selector(deleteTapped)),
        ])

        tools.orientation = .horizontal
        tools.spacing = 4
        tools.distribution = .fillEqually

        let stack = NSStackView(views: [
            caption("Guide title"), titleField,
            caption("Summary"), summaryField,
            scroll, tools,
        ])

        stack.orientation = .vertical
        stack.spacing = 4
        stack.alignment = .leading
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 12, bottom: 10, right: 12)
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.setHuggingPriority(.defaultLow, for: .vertical)
        panel.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: panel.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: panel.trailingAnchor),
            stack.topAnchor.constraint(equalTo: panel.topAnchor),
            stack.bottomAnchor.constraint(equalTo: panel.bottomAnchor),
            titleField.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24),
            summaryField.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24),
            scroll.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24),
            tools.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -24),
        ])

        return panel
    }

    private func buildEditor() -> NSView {
        let panel = NSView()
        panel.wantsLayer = true
        panel.layer?.backgroundColor = Theme.background.cgColor

        stepField.font = .systemFont(ofSize: 14)
        stepField.textColor = Theme.text
        stepField.backgroundColor = Theme.raised
        stepField.isRichText = false
        stepField.delegate = self

        let stepScroll = NSScrollView()
        stepScroll.documentView = stepField
        stepScroll.hasVerticalScroller = true
        stepScroll.drawsBackground = false

        noteField.placeholderString = "Note, shown under the step"
        noteField.delegate = self

        let framing = NSStackView()
        framing.orientation = .horizontal
        framing.spacing = 4
        framing.addArrangedSubview(caption("Framing"))

        for (title, variant) in [
            ("Full", CropVariant.full),
            ("Window", CropVariant.window),
            ("Focus", CropVariant.focus),
            ("Close", CropVariant.close),
        ] {
            let control = button(title, #selector(variantTapped(_:)))
            control.tag = variantTag(variant)
            variantButtons.append(control)
            framing.addArrangedSubview(control)
        }

        animateButton = button("Animate", #selector(animateTapped))
        framing.addArrangedSubview(animateButton)
        framing.addArrangedSubview(NSView())

        let tools = NSStackView()
        tools.orientation = .horizontal
        tools.spacing = 4

        for (title, tool) in [
            ("Select", PreviewView.Tool.select),
            ("Box", .box),
            ("Arrow", .arrow),
            ("Mark", .highlight),
            ("Blur", .blur),
            ("Crop", .crop),
            ("Point", .marker),
        ] {
            let control = button(title, #selector(toolTapped(_:)))
            control.tag = toolTag(tool)
            tools.addArrangedSubview(control)
        }

        tools.addArrangedSubview(button("Clear callouts", #selector(clearCalloutsTapped)))
        tools.addArrangedSubview(button("Words only", #selector(wordsOnlyTapped)))
        tools.addArrangedSubview(NSView())

        preview.onRegion = { [weak self] region, tool in self?.regionDrawn(region, tool) }
        preview.onPoint = { [weak self] point, tool in self?.pointPicked(point, tool) }

        let stack = NSStackView(views: [
            caption("Step text"), stepScroll,
            caption("Note, shown under the step"), noteField,
            framing, tools, preview,
        ])

        stack.orientation = .vertical
        stack.spacing = 6
        stack.alignment = .leading
        stack.edgeInsets = NSEdgeInsets(top: 12, left: 14, bottom: 12, right: 14)
        stack.translatesAutoresizingMaskIntoConstraints = false
        panel.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: panel.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: panel.trailingAnchor),
            stack.topAnchor.constraint(equalTo: panel.topAnchor),
            stack.bottomAnchor.constraint(equalTo: panel.bottomAnchor),
            stepScroll.heightAnchor.constraint(equalToConstant: 56),
            stepScroll.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
            noteField.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
            framing.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
            tools.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
            preview.widthAnchor.constraint(equalTo: stack.widthAnchor, constant: -28),
        ])

        return panel
    }

    private func buildStatusBar() -> NSView {
        let row = NSStackView(views: [statusLabel, NSView(), countLabel])
        row.orientation = .horizontal
        row.edgeInsets = NSEdgeInsets(top: 2, left: 12, bottom: 2, right: 12)
        row.wantsLayer = true
        row.layer?.backgroundColor = Theme.panel.cgColor

        statusLabel.textColor = Theme.muted
        statusLabel.font = .systemFont(ofSize: 11)
        countLabel.textColor = Theme.muted
        countLabel.font = .systemFont(ofSize: 11)
        return row
    }

    private func button(_ title: String, _ action: Selector) -> NSButton {
        let control = NSButton(title: title, target: self, action: action)
        control.bezelStyle = .rounded
        control.controlSize = .regular
        return control
    }

    private func caption(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 10.5)
        label.textColor = Theme.muted
        return label
    }

    private func variantTag(_ variant: CropVariant) -> Int {
        switch variant {
        case .full: return 1
        case .window: return 2
        case .focus: return 3
        case .close: return 4
        }
    }

    private func variant(from tag: Int) -> CropVariant {
        switch tag {
        case 1: return .full
        case 2: return .window
        case 4: return .close
        default: return .focus
        }
    }

    private func toolTag(_ tool: PreviewView.Tool) -> Int {
        switch tool {
        case .select: return 10
        case .box: return 11
        case .arrow: return 12
        case .highlight: return 13
        case .blur: return 14
        case .crop: return 15
        case .marker: return 16
        case .label: return 17
        }
    }

    private func tool(from tag: Int) -> PreviewView.Tool {
        switch tag {
        case 11: return .box
        case 12: return .arrow
        case 13: return .highlight
        case 14: return .blur
        case 15: return .crop
        case 16: return .marker
        case 17: return .label
        default: return .select
        }
    }

    // ------------------------------------------------------------------ the list

    func numberOfRows(in tableView: NSTableView) -> Int { guide.Steps.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard row < guide.Steps.count else { return nil }
        let step = guide.Steps[row]

        let view = tableView.makeView(
            withIdentifier: NSUserInterfaceItemIdentifier("row"),
            owner: self) as? StepRowView ?? StepRowView()

        view.identifier = NSUserInterfaceItemIdentifier("row")
        view.show(step: step, number: number(of: row), picture: thumbnail(step), heading: step.Kind == .heading)
        return view
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        loadSelectedStep()
    }

    private var selectedStep: Step? {
        let row = table.selectedRow
        return row >= 0 && row < guide.Steps.count ? guide.Steps[row] : nil
    }

    private func number(of row: Int) -> Int {
        var count = 0
        var index = 0

        while index <= row, index < guide.Steps.count {
            if guide.Steps[index].Kind != .heading { count += 1 }
            index += 1
        }

        return count
    }

    private func thumbnail(_ step: Step) -> NSImage? {
        if let cached = thumbnails[step.Id] { return cached }
        guard let path = guide.imagePath(step), let image = ImageFile.load(path) else { return nil }

        let small = Renderer.resize(image, maxWidth: 170)
        let picture = NSImage(cgImage: small, size: NSSize(width: small.width, height: small.height))
        thumbnails[step.Id] = picture
        return picture
    }

    private func dropThumbnail(_ step: Step) { thumbnails.removeValue(forKey: step.Id) }

    // ------------------------------------------------------------------ text editing

    func controlTextDidChange(_ notification: Notification) {
        guard !loading else { return }

        if notification.object as? NSTextField === titleField {
            guide.Title = titleField.stringValue
            markDirty()
        } else if notification.object as? NSTextField === summaryField {
            guide.Summary = summaryField.stringValue
            markDirty()
        } else if notification.object as? NSTextField === noteField, let step = selectedStep {
            step.Notes = noteField.stringValue
            markDirty()
        }
    }

    func textDidChange(_ notification: Notification) {
        guard !loading, let step = selectedStep else { return }
        step.Text = stepField.string
        markDirty()

        let row = table.selectedRow
        if row >= 0 { table.reloadData(forRowIndexes: IndexSet(integer: row), columnIndexes: IndexSet(integer: 0)) }
    }

    private func loadSelectedStep() {
        loading = true
        let step = selectedStep

        stepField.string = step?.Text ?? ""
        noteField.stringValue = step?.Notes ?? ""

        let canAnimate = step != nil && StepAnimator.canAnimate(step!)
        animateButton.isEnabled = canAnimate
        animateButton.state = (canAnimate && step!.Animate) ? .on : .off
        animateButton.bezelColor = (canAnimate && step!.Animate) ? Theme.accent : nil

        for control in variantButtons { control.isEnabled = step?.hasImage ?? false }

        loading = false
        refreshPreview()
    }

    private func refreshPreview() {
        guard let step = selectedStep, step.hasImage else {
            preview.emptyMessage = selectedStep == nil ? "Nothing recorded yet" : "This step has no screenshot"
            preview.emptyHint = selectedStep == nil
                ? "Press Record, or F9 from anywhere, then do the task once."
                : "Add a note or a heading to give the reader some context."
            preview.show(nil, origin: .zero)
            return
        }

        guard let path = guide.imagePath(step), let source = ImageFile.load(path) else {
            preview.emptyMessage = "The screenshot is missing"
            preview.emptyHint = ""
            preview.show(nil, origin: .zero)
            return
        }

        let crop = Renderer.effectiveCrop(
            step: step,
            imageSize: CGSize(width: source.width, height: source.height),
            padding: settings.zoomPadding)

        let composed = Renderer.compose(
            step: step,
            source: source,
            markerHex: settings.markerColor,
            padding: settings.zoomPadding)

        preview.show(composed, origin: crop.origin)
    }

    private func markDirty() {
        guide.dirty = true
        updateCaption()
    }

    private func updateCaption() {
        let name = guide.filePath?.deletingPathExtension().lastPathComponent ?? "Untitled"
        window?.title = "Stepwright   \(name)\(guide.dirty ? " (unsaved)" : "")"

        let count = guide.visible.filter { $0.Kind != .heading }.count
        let hidden = guide.Steps.filter { $0.Skip }.count
        countLabel.stringValue = (count == 1 ? "1 step" : "\(count) steps")
            + (hidden > 0 ? "   \(hidden) hidden" : "")
    }

    private func status(_ message: String) { statusLabel.stringValue = message }

    private func warn(_ message: String) {
        let alert = NSAlert()
        alert.messageText = "Stepwright"
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.runModal()
    }

    // ------------------------------------------------------------------ the guide

    func newGuide(askToSave: Bool) {
        if askToSave, !confirmDiscard() { return }

        guide = Guide()
        guide.Author = settings.author
        guide.mediaFolder = GuideStore.createWorkFolder().appendingPathComponent("media")

        try? FileManager.default.createDirectory(at: guide.mediaFolder!, withIntermediateDirectories: true)

        thumbnails.removeAll()
        table.reloadData()
        loadGuideFields()
        status("Press Record, or F9 from anywhere, and Stepwright writes the steps for you.")
    }

    private func loadGuideFields() {
        loading = true
        titleField.stringValue = guide.Title
        summaryField.stringValue = guide.Summary
        loading = false
        updateCaption()
    }

    private func confirmDiscard() -> Bool {
        guard guide.dirty else { return true }

        let alert = NSAlert()
        alert.messageText = "This guide has changes that are not saved."
        alert.informativeText = "Save it now?"
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Discard")
        alert.addButton(withTitle: "Cancel")

        switch alert.runModal() {
        case .alertFirstButtonReturn: return saveGuide(saveAs: false)
        case .alertSecondButtonReturn: return true
        default: return false
        }
    }

    @discardableResult
    private func saveGuide(saveAs: Bool) -> Bool {
        var target = guide.filePath

        if saveAs || target == nil {
            let panel = NSSavePanel()
            panel.allowedContentTypes = []
            panel.nameFieldStringValue = GuideStore.suggestFileName(guide) + "." + GuideStore.fileExtension
            panel.directoryURL = libraryFolder()

            guard panel.runModal() == .OK, let chosen = panel.url else { return false }
            target = chosen
        }

        guard let target else { return false }

        do {
            try GuideStore.save(guide, to: target)
            updateCaption()
            status("Saved to " + target.path)
            return true
        } catch {
            warn("The guide could not be saved. " + error.localizedDescription)
            return false
        }
    }

    private func libraryFolder() -> URL {
        let folder = settings.libraryFolder
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    @objc func newTapped() { newGuide(askToSave: true) }

    @objc func openTapped() {
        guard confirmDiscard() else { return }

        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.directoryURL = libraryFolder()

        guard panel.runModal() == .OK, let url = panel.url else { return }

        do {
            guide = try GuideStore.load(url)
            thumbnails.removeAll()
            table.reloadData()
            loadGuideFields()
            status("Opened " + url.lastPathComponent)
        } catch {
            warn("That guide could not be opened. " + error.localizedDescription)
        }
    }

    @objc func saveTapped() { saveGuide(saveAs: false) }

    // ------------------------------------------------------------------ recording

    @objc func recordTapped() {
        switch recorder.state {
        case .recording:
            recorder.pause()
            bar?.paused = true
            recordButton.title = "Resume"
            status("Recording paused.")
        case .paused:
            recorder.resume()
            bar?.paused = false
            recordButton.title = "Pause"
            status("Recording again.")
        case .idle:
            startRecording()
        }
    }

    private func startRecording() {
        guard Permissions.allGranted else {
            showPermissions()
            return
        }

        guard let folder = guide.mediaFolder else { return }

        recordButton.title = "Pause"
        finishButton.isEnabled = true
        window?.miniaturize(nil)

        let countdown = max(0, settings.countdownSeconds)
        DispatchQueue.main.asyncAfter(deadline: .now() + Double(countdown)) { [weak self] in
            self?.beginRecordingNow(folder)
        }
    }

    private func beginRecordingNow(_ folder: URL) {
        if let problem = recorder.start(mediaFolder: folder) {
            window?.deminiaturize(nil)
            resetRecordButtons()
            warn(problem)
            return
        }

        let bar = RecorderBar()
        bar.elapsedSource = { [weak self] in self?.recorder.elapsed ?? 0 }
        bar.stepCountSource = { [weak self] in
            self?.guide.Steps.filter { $0.Kind != .heading }.count ?? 0
        }

        bar.onPause = { [weak self] in self?.recordTapped() }
        bar.onStop = { [weak self] in self?.finishTapped() }
        bar.onShot = { [weak self] in self?.recorder.captureManualShot() }
        bar.placeBottomCentre()
        bar.orderFrontRegardless()

        self.bar = bar
        status("Recording. Everything you click and type becomes a step.")
    }

    @objc func finishTapped() {
        guard recorder.state != .idle else { return }

        recorder.stop()
        bar?.close(finished: true)
        bar = nil

        resetRecordButtons()
        window?.deminiaturize(nil)
        window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)

        table.reloadData()
        if !guide.Steps.isEmpty { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false) }

        markDirty()
        status("Recording finished with \(guide.Steps.count) steps. Tidy the wording, then export.")
    }

    private func resetRecordButtons() {
        recordButton.title = "Record"
        finishButton.isEnabled = false
    }

    private func stepArrived(_ step: Step) {
        guide.Steps.append(step)
        guide.dirty = true
        table.reloadData()
        bar?.refresh()
        updateCaption()
    }

    private func stepRewritten(_ step: Step) {
        guard let row = guide.Steps.firstIndex(where: { $0 === step }) else { return }
        table.reloadData(forRowIndexes: IndexSet(integer: row), columnIndexes: IndexSet(integer: 0))
        if step === selectedStep { loadSelectedStep() }
    }

    /// F9 starts or pauses, F10 finishes, F8 captures, from anywhere on the desktop.
    private func watchForShortcuts() {
        let handler: (NSEvent) -> Void = { [weak self] event in
            guard let self else { return }

            switch event.keyCode {
            case 101: self.recordTapped()
            case 109: self.finishTapped()
            case 100: self.recorder.captureManualShot()
            default: break
            }
        }

        globalKeys = NSEvent.addGlobalMonitorForEvents(matching: [.keyDown]) { handler($0) }
        localKeys = NSEvent.addLocalMonitorForEvents(matching: [.keyDown]) { event in
            if [100, 101, 109].contains(event.keyCode) {
                handler(event)
                return nil
            }

            return event
        }
    }

    // ------------------------------------------------------------------ step editing

    @objc func addNoteTapped() { insertStep(kind: .note) }

    @objc func addHeadingTapped() { insertStep(kind: .heading) }

    private func insertStep(kind: StepKind) {
        let step = Step()
        step.Kind = kind
        step.Text = kind == .heading ? "New section" : "Add your own note here."
        step.OriginalText = step.Text
        step.ShowClickMarker = false
        step.AutoZoom = false

        let row = table.selectedRow
        let index = row < 0 ? guide.Steps.count : row + 1
        guide.Steps.insert(step, at: index)

        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        markDirty()
    }

    @objc func moveUpTapped() { moveStep(-1) }

    @objc func moveDownTapped() { moveStep(1) }

    private func moveStep(_ direction: Int) {
        let row = table.selectedRow
        let target = row + direction

        guard row >= 0, target >= 0, target < guide.Steps.count else { return }
        guide.Steps.swapAt(row, target)

        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: target), byExtendingSelection: false)
        markDirty()
    }

    @objc func duplicateTapped() {
        guard let step = selectedStep else { return }
        let index = table.selectedRow + 1

        guide.Steps.insert(step.copy(), at: index)
        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        markDirty()
    }

    @objc func hideTapped() {
        guard let step = selectedStep else { return }
        step.Skip = !step.Skip
        table.reloadData()
        markDirty()
    }

    @objc func deleteTapped() {
        let row = table.selectedRow
        guard row >= 0, row < guide.Steps.count else { return }

        dropThumbnail(guide.Steps[row])
        guide.Steps.remove(at: row)
        table.reloadData()

        if !guide.Steps.isEmpty {
            table.selectRowIndexes(IndexSet(integer: min(row, guide.Steps.count - 1)), byExtendingSelection: false)
        }

        markDirty()
    }

    /// Folds a step into the one before it, keeping the earlier picture.
    @objc func mergeTapped() {
        let row = table.selectedRow
        guard row >= 1 else { return }

        let current = guide.Steps[row]
        let previous = guide.Steps[row - 1]

        guard previous.Kind != .heading, current.Kind != .heading else {
            status("A section heading cannot be merged.")
            return
        }

        var tail = current.Text.trimmingCharacters(in: .whitespaces)
        if !tail.isEmpty {
            var head = previous.Text.trimmingCharacters(in: .whitespaces)
            if head.hasSuffix(".") { head.removeLast() }
            tail = tail.prefix(1).lowercased() + tail.dropFirst()
            previous.Text = head + ", then " + tail
        }

        if !current.Notes.isEmpty {
            previous.Notes = previous.Notes.isEmpty ? current.Notes : previous.Notes + " " + current.Notes
        }

        if !previous.hasImage, current.hasImage {
            previous.Image = current.Image
            previous.ClickPoint = current.ClickPoint
            previous.ElementArea = current.ElementArea
            previous.Crop = current.Crop
            current.Image = ""
        }

        dropThumbnail(current)
        dropThumbnail(previous)
        guide.Steps.remove(at: row)

        table.reloadData()
        table.selectRowIndexes(IndexSet(integer: row - 1), byExtendingSelection: false)
        markDirty()
    }

    // ------------------------------------------------------------------ framing and callouts

    @objc func variantTapped(_ sender: NSButton) {
        let chosen = variant(from: sender.tag)
        lastVariant = chosen

        guard let step = selectedStep, step.hasImage else { return }
        apply(variant: chosen, to: step)

        dropThumbnail(step)
        markDirty()
        refreshPreview()
        table.reloadData()
    }

    private func apply(variant chosen: CropVariant, to step: Step) {
        switch chosen {
        case .focus:
            step.Crop = nil
            step.AutoZoom = true
        case .full:
            step.Crop = nil
            step.AutoZoom = false
        default:
            guard let path = guide.imagePath(step), let source = ImageFile.load(path) else { return }
            let size = CGSize(width: source.width, height: source.height)

            step.Crop = RectI(Renderer.variantCrop(
                step: step,
                imageSize: size,
                variant: chosen,
                padding: settings.zoomPadding))

            step.AutoZoom = false
        }
    }

    @objc func animateTapped() {
        guard let step = selectedStep, StepAnimator.canAnimate(step) else { return }

        step.Animate = !step.Animate
        animateButton.state = step.Animate ? .on : .off
        animateButton.bezelColor = step.Animate ? Theme.accent : nil

        markDirty()
        table.reloadData()

        status(step.Animate
            ? "This step will export as an animation. Web pages and Markdown show it; a document uses the still picture."
            : "This step will export as a still picture.")
    }

    @objc func toolTapped(_ sender: NSButton) {
        preview.tool = tool(from: sender.tag)
        preview.drawColor = NSColor.fromHex(settings.markerColor)
        status("Tool: " + (sender.title))
    }

    @objc func clearCalloutsTapped() {
        guard let step = selectedStep, !step.Annotations.isEmpty else { return }
        step.Annotations.removeAll()
        markDirty()
        refreshPreview()
    }

    private func regionDrawn(_ region: CGRect, _ tool: PreviewView.Tool) {
        guard let step = selectedStep else { return }

        switch tool {
        case .crop:
            step.Crop = RectI(region)
            step.AutoZoom = false
        case .blur, .box, .highlight, .arrow:
            let annotation = Annotation()
            annotation.Area = RectI(region)
            annotation.Color = settings.markerColor

            switch tool {
            case .blur: annotation.Kind = .blur
            case .box: annotation.Kind = .rectangle
            case .highlight: annotation.Kind = .highlight
            default: annotation.Kind = .arrow
            }

            step.Annotations.append(annotation)
        default:
            return
        }

        dropThumbnail(step)
        markDirty()
        refreshPreview()
    }

    private func pointPicked(_ point: CGPoint, _ tool: PreviewView.Tool) {
        guard let step = selectedStep else { return }

        switch tool {
        case .marker:
            step.ClickPoint = PointI(point)
            step.ShowClickMarker = true
        case .select:
            guard let hit = step.Annotations.last(where: {
                $0.Area.rect.insetBy(dx: -12, dy: -12).contains(point)
            }) else { return }

            step.Annotations.removeAll { $0 === hit }
        default:
            return
        }

        markDirty()
        refreshPreview()
    }

    // ------------------------------------------------------------------ exports

    @objc func exportChosen(_ sender: NSPopUpButton) {
        switch sender.indexOfSelectedItem {
        case 1: exportHtml(embed: true)
        case 2: exportHtml(embed: false)
        case 3: exportMarkdown()
        case 4: exportPdf()
        case 5: exportReel()
        case 6: copyRich()
        case 7: copyPlain()
        default: break
        }

        sender.selectItem(at: 0)
    }

    private func savePanel(_ suggestion: String) -> URL? {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = suggestion
        panel.directoryURL = libraryFolder()
        return panel.runModal() == .OK ? panel.url : nil
    }

    private func runExport(_ work: @escaping () throws -> URL) {
        status("Building the export...")

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            do {
                let url = try work()
                DispatchQueue.main.async {
                    self?.status("Exported to " + url.path)
                    NSWorkspace.shared.activateFileViewerSelecting([url])
                }
            } catch {
                DispatchQueue.main.async { self?.warn("The export failed. " + error.localizedDescription) }
            }
        }
    }

    private func exportHtml(embed: Bool) {
        guard let url = savePanel(GuideStore.suggestFileName(guide) + ".html") else { return }
        let guide = self.guide
        let settings = self.settings

        runExport {
            var options = HtmlOptions()
            options.format = FormatProfiles.find(settings.exportFormat)
            options.embedImages = embed
            try HtmlExporter.export(guide: guide, settings: settings, to: url, options: options)
            return url
        }
    }

    /// Sends the guide straight into a knowledge base.
    @objc func publishChosen(_ sender: NSPopUpButton) {
        let index = sender.indexOfSelectedItem
        sender.selectItem(at: 0)

        guard index == 1 || index == 2 else { return }
        let destination: PublishDestination = index == 1 ? .hudu : .confluence

        guard !guide.Steps.isEmpty else {
            warn("There is nothing to publish yet.")
            return
        }

        let ready = destination == .hudu ? settings.hasHudu : settings.hasConfluence

        guard ready else {
            let name = destination == .hudu ? "Hudu" : "Confluence"
            warn("\(name) is not set up yet. Add the connection under Settings, on the Publishing page.")
            settingsTapped()
            return
        }

        let controller = PublishWindow(settings: settings, guide: guide, destination: destination)
        controller.showModal()
        status("Ready.")
    }

    /// Turns a step into words only, setting the picture aside rather than destroying it.
    @objc func wordsOnlyTapped() {
        guard let step = selectedStep else { return }

        if step.hasImage {
            step.HiddenImage = step.Image
            step.Image = ""
            step.Animate = false
            dropThumbnail(step)
            status("This step is now words only. Press again to bring the picture back.")
        } else if !step.HiddenImage.isEmpty {
            step.Image = step.HiddenImage
            step.HiddenImage = ""
            dropThumbnail(step)
            status("The picture is back.")
        } else {
            status("This step never had a picture.")
            return
        }

        markDirty()
        loadSelectedStep()
        table.reloadData()
    }

    private func exportMarkdown() {
        guard let url = savePanel(GuideStore.suggestFileName(guide) + ".md") else { return }
        let guide = self.guide
        let settings = self.settings

        runExport {
            try MarkdownExporter.export(guide: guide, settings: settings, to: url)
            return url
        }
    }

    private func exportPdf() {
        guard let url = savePanel(GuideStore.suggestFileName(guide) + ".pdf") else { return }
        let guide = self.guide
        let settings = self.settings

        runExport {
            try PdfExporter.export(guide: guide, settings: settings, to: url)
            return url
        }
    }

    private func exportReel() {
        guard guide.visible.contains(where: { $0.hasImage }) else {
            warn("There are no screenshots to animate yet.")
            return
        }

        guard let url = savePanel(GuideStore.suggestFileName(guide) + ".gif") else { return }
        let guide = self.guide
        let settings = self.settings

        runExport {
            guard let data = GuideAnimator.build(
                guide: guide,
                settings: settings,
                maxWidth: max(640, settings.gifWidth)) else {
                throw Failure.message("There was nothing to animate.")
            }

            try data.write(to: url)
            return url
        }
    }

    private func copyRich() {
        var options = HtmlOptions()
        options.fragment = true
        options.format = FormatProfiles.find(settings.exportFormat)
        options.embedImages = true

        let html = HtmlExporter.build(guide: guide, settings: settings, options: options)
        let board = NSPasteboard.general

        board.clearContents()
        board.setString(html, forType: .html)
        board.setString(MarkdownExporter.plainText(guide), forType: .string)
        status("Copied. Paste it into your knowledge base or an email.")
    }

    private func copyPlain() {
        let board = NSPasteboard.general
        board.clearContents()
        board.setString(MarkdownExporter.plainText(guide), forType: .string)
        status("The steps are on the clipboard as plain text.")
    }

    // ------------------------------------------------------------------ the assistant

    @objc func improveTapped() {
        guard settings.aiEnabled else {
            warn("Turn the assistant on in Settings first, then choose a service and paste a key.")
            settingsTapped()
            return
        }

        guard !guide.Steps.isEmpty else { return }

        let guide = self.guide
        let settings = self.settings
        var pictures: [String: Data] = [:]

        if settings.aiSendScreenshots {
            status("Preparing the screenshots...")

            for step in guide.Steps where step.hasImage {
                if let picture = Renderer.render(guide: guide, step: step, settings: settings, maxWidth: 1100),
                   let data = ImageFile.jpegData(picture, quality: 0.72) {
                    pictures[step.Id] = data
                }
            }
        }

        Task { @MainActor in
            do {
                let changed = try await Assistant.improve(
                    guide: guide,
                    settings: settings,
                    pictureFor: { step in pictures[step.Id] },
                    progress: { [weak self] message in
                        Task { @MainActor in self?.status(message) }
                    })

                if guide.Title.isEmpty || guide.Title == "Untitled guide" {
                    let (title, summary) = try await Assistant.suggestHeading(guide: guide, settings: settings)
                    if !title.isEmpty {
                        guide.Title = title
                        if guide.Summary.isEmpty { guide.Summary = summary }
                        self.loadGuideFields()
                    }
                }

                self.table.reloadData()
                self.loadSelectedStep()
                self.markDirty()
                self.status(changed == 0
                    ? "The assistant left every step as it was."
                    : "The assistant rewrote \(changed) steps.")
            } catch {
                self.warn("The assistant could not finish. " + error.localizedDescription)
            }
        }
    }

    @objc func permissionsTapped() { showPermissions() }

    /// Opens the permissions window, or brings the open one to the front.
    private func showPermissions() {
        if let existing = permissionsWindow {
            existing.refresh()
            existing.window?.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let controller = PermissionsWindow()
        controller.onFinished = { [weak self] in
            self?.permissionsWindow = nil
            self?.updatePermissionNotice()
        }

        permissionsWindow = controller
        controller.showWindow(nil)
        controller.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    /// Says up front what is still missing, rather than waiting for a recording to fail.
    func updatePermissionNotice() {
        let missing = Permissions.missing

        if missing.isEmpty {
            status("Press Record, or F9 from anywhere, and Stepwright writes the steps for you.")
            return
        }

        let names = missing.map { $0.title }.joined(separator: ", ")
        status("Waiting on permission: \(names). Open Permissions to sort it out.")
    }

    @objc func settingsTapped() {
        let controller = SettingsWindow(settings: settings)
        controller.showModal(over: window)
        refreshPreview()
        status("Settings saved.")
    }
}
