import AppKit
import Foundation

/// The preferences, laid out as plain stacked rows so nothing can be clipped.
final class SettingsWindow: NSWindowController {
    private let settings: Settings

    private let author = NSTextField()
    private let countdown = NSTextField()
    private let allDisplays = NSButton(checkboxWithTitle: "Capture every display instead of the one in use", target: nil, action: nil)
    private let keyboard = NSButton(checkboxWithTitle: "Record what is typed", target: nil, action: nil)
    private let scroll = NSButton(checkboxWithTitle: "Record scrolling", target: nil, action: nil)
    private let drag = NSButton(checkboxWithTitle: "Record dragging", target: nil, action: nil)
    private let redact = NSButton(checkboxWithTitle: "Never store anything typed into a password box", target: nil, action: nil)
    private let autoZoom = NSButton(checkboxWithTitle: "Zoom each screenshot to the part that was used", target: nil, action: nil)
    private let marker = NSButton(checkboxWithTitle: "Mark where the click landed", target: nil, action: nil)
    private let outline = NSButton(checkboxWithTitle: "Outline the control that was used", target: nil, action: nil)

    private let aiEnabled = NSButton(checkboxWithTitle: "Use the assistant", target: nil, action: nil)
    private let aiProvider = NSPopUpButton()
    private let aiBaseUrl = NSTextField()
    private let aiModel = NSComboBox()
    private let aiKey = NSSecureTextField()
    private let aiPictures = NSButton(checkboxWithTitle: "Let the assistant see each screenshot", target: nil, action: nil)
    private let aiNotes = NSButton(checkboxWithTitle: "Write a note under a step when it helps", target: nil, action: nil)
    private let aiResult = NSTextField(labelWithString: "")
    private let aiHint = NSTextField(labelWithString: "")

    private var keyEdited = false

    init(settings: Settings) {
        self.settings = settings

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 620, height: 720),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false)

        window.title = "Stepwright settings"
        super.init(window: window)

        window.appearance = NSAppearance(named: .darkAqua)
        build()
        load()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func showModal(over parent: NSWindow?) {
        guard let window else { return }
        NSApp.runModal(for: window)
        window.orderOut(nil)
    }

    private func build() {
        guard let window, let content = window.contentView else { return }

        let tabs = NSTabView()
        tabs.translatesAutoresizingMaskIntoConstraints = false

        tabs.addTabViewItem(page("Recording", views: [
            caption("Your name"), author,
            caption("Countdown before recording, in seconds"), countdown,
            allDisplays, keyboard, scroll, drag, redact,
        ]))

        tabs.addTabViewItem(page("Look", views: [autoZoom, marker, outline]))

        aiProvider.addItems(withTitles: AiProviders.all.map { $0.label })
        aiProvider.target = self
        aiProvider.action = #selector(providerChanged)

        aiModel.isEditable = true
        aiHint.font = .systemFont(ofSize: 10.5)
        aiHint.textColor = Theme.muted
        aiResult.font = .systemFont(ofSize: 11)
        aiResult.textColor = Theme.muted
        aiResult.lineBreakMode = .byWordWrapping
        aiResult.maximumNumberOfLines = 3

        let findModels = NSButton(title: "Find models", target: self, action: #selector(findModelsTapped))
        let test = NSButton(title: "Test the connection", target: self, action: #selector(testTapped))
        let keyLink = NSButton(title: "Where to get a key", target: self, action: #selector(keyPageTapped))
        keyLink.bezelStyle = .inline

        tabs.addTabViewItem(page("Assistant", views: [
            aiEnabled,
            caption("Service"), aiProvider,
            caption("Address"), aiBaseUrl,
            caption("Key, kept in your keychain"), aiKey,
            row([aiHint, keyLink]),
            caption("Model"), row([aiModel, findModels]),
            aiPictures, aiNotes,
            row([test]), aiResult,
        ]))

        let save = NSButton(title: "Save", target: self, action: #selector(saveTapped))
        save.keyEquivalent = "\r"
        save.bezelColor = Theme.accent

        let cancel = NSButton(title: "Cancel", target: self, action: #selector(cancelTapped))

        let buttons = NSStackView(views: [NSView(), cancel, save])
        buttons.orientation = .horizontal
        buttons.spacing = 8
        buttons.translatesAutoresizingMaskIntoConstraints = false

        content.addSubview(tabs)
        content.addSubview(buttons)

        NSLayoutConstraint.activate([
            tabs.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 14),
            tabs.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -14),
            tabs.topAnchor.constraint(equalTo: content.topAnchor, constant: 14),
            buttons.topAnchor.constraint(equalTo: tabs.bottomAnchor, constant: 12),
            buttons.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 14),
            buttons.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -14),
            buttons.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -14),
        ])
    }

    private func page(_ title: String, views: [NSView]) -> NSTabViewItem {
        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 6
        stack.edgeInsets = NSEdgeInsets(top: 16, left: 16, bottom: 16, right: 16)
        stack.translatesAutoresizingMaskIntoConstraints = false

        for view in views where view is NSTextField || view is NSPopUpButton {
            view.widthAnchor.constraint(equalToConstant: 540).isActive = true
        }

        let scroll = NSScrollView()
        scroll.documentView = stack
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = false

        let item = NSTabViewItem(identifier: title)
        item.label = title
        item.view = scroll
        return item
    }

    private func row(_ views: [NSView]) -> NSStackView {
        let stack = NSStackView(views: views)
        stack.orientation = .horizontal
        stack.spacing = 8
        return stack
    }

    private func caption(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 10.5)
        label.textColor = Theme.muted
        return label
    }

    private func load() {
        author.stringValue = settings.author
        countdown.stringValue = "\(settings.countdownSeconds)"
        allDisplays.state = settings.captureAllDisplays ? .on : .off
        keyboard.state = settings.captureKeyboard ? .on : .off
        scroll.state = settings.captureScroll ? .on : .off
        drag.state = settings.captureDrag ? .on : .off
        redact.state = settings.redactPasswords ? .on : .off
        autoZoom.state = settings.autoZoom ? .on : .off
        marker.state = settings.showClickMarker ? .on : .off
        outline.state = settings.showElementOutline ? .on : .off

        aiEnabled.state = settings.aiEnabled ? .on : .off
        aiBaseUrl.stringValue = settings.aiBaseUrl
        aiModel.stringValue = settings.aiModel
        aiKey.stringValue = settings.hasAiKey ? String(repeating: "*", count: 24) : ""
        aiPictures.state = settings.aiSendScreenshots ? .on : .off
        aiNotes.state = settings.aiWriteNotes ? .on : .off

        if let index = AiProviders.all.firstIndex(where: { $0.id == settings.aiProvider }) {
            aiProvider.selectItem(at: index)
        }

        showHint()
    }

    private var chosenProvider: AiProvider {
        AiProviders.all[max(0, min(aiProvider.indexOfSelectedItem, AiProviders.all.count - 1))]
    }

    private func showHint() { aiHint.stringValue = chosenProvider.hint }

    /// The values the buttons on this page should be tried against.
    private func probe() -> Settings {
        let probe = Settings()
        probe.aiProvider = chosenProvider.id
        probe.aiBaseUrl = aiBaseUrl.stringValue.trimmingCharacters(in: .whitespaces)
        probe.aiModel = aiModel.stringValue.trimmingCharacters(in: .whitespaces)

        if keyEdited, !aiKey.stringValue.isEmpty {
            probe.aiKey = aiKey.stringValue.trimmingCharacters(in: .whitespaces)
        }

        return probe
    }

    @objc private func providerChanged() {
        aiBaseUrl.stringValue = chosenProvider.baseUrl
        aiModel.removeAllItems()
        aiModel.stringValue = chosenProvider.model
        showHint()
    }

    @objc private func keyPageTapped() {
        guard !chosenProvider.keyPage.isEmpty, let url = URL(string: chosenProvider.keyPage) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func findModelsTapped() {
        keyEdited = keyEdited || !aiKey.stringValue.contains("*")
        aiResult.stringValue = "Asking the service which models it has..."

        let settings = probe()

        Task { @MainActor in
            do {
                let models = try await AiClient.listModels(settings: settings)

                if models.isEmpty {
                    self.aiResult.stringValue = "The service returned no models. Type the name in yourself."
                    return
                }

                let current = self.aiModel.stringValue
                self.aiModel.removeAllItems()
                self.aiModel.addItems(withObjectValues: models)

                if models.contains(current) {
                    self.aiModel.stringValue = current
                }

                self.aiResult.stringValue = "Found \(models.count) models. Open the list to choose one."
            } catch {
                self.aiResult.stringValue = error.localizedDescription
            }
        }
    }

    @objc private func testTapped() {
        keyEdited = keyEdited || !aiKey.stringValue.contains("*")
        aiResult.stringValue = "Talking to the service..."

        let settings = probe()

        Task { @MainActor in
            do {
                let reply = try await Assistant.test(settings: settings)
                self.aiResult.stringValue = "Connected. The model said: " + String(reply.prefix(80))
            } catch {
                self.aiResult.stringValue = error.localizedDescription
            }
        }
    }

    @objc private func saveTapped() {
        settings.author = author.stringValue.trimmingCharacters(in: .whitespaces)
        settings.countdownSeconds = Int(countdown.stringValue) ?? 3
        settings.captureAllDisplays = allDisplays.state == .on
        settings.captureKeyboard = keyboard.state == .on
        settings.captureScroll = scroll.state == .on
        settings.captureDrag = drag.state == .on
        settings.redactPasswords = redact.state == .on
        settings.autoZoom = autoZoom.state == .on
        settings.showClickMarker = marker.state == .on
        settings.showElementOutline = outline.state == .on

        settings.aiEnabled = aiEnabled.state == .on
        settings.aiProvider = chosenProvider.id
        settings.aiBaseUrl = aiBaseUrl.stringValue.trimmingCharacters(in: .whitespaces)
        settings.aiModel = aiModel.stringValue.trimmingCharacters(in: .whitespaces)
        settings.aiSendScreenshots = aiPictures.state == .on
        settings.aiWriteNotes = aiNotes.state == .on

        if keyEdited || !aiKey.stringValue.contains("*") {
            settings.aiKey = aiKey.stringValue.trimmingCharacters(in: .whitespaces)
        }

        NSApp.stopModal()
    }

    @objc private func cancelTapped() { NSApp.stopModal() }
}
