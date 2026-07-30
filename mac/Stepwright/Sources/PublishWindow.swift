import AppKit
import Foundation

/// Sends the finished guide straight into a knowledge base. The two places want the document
/// written differently and the pictures handled differently, so each carries its own format
/// and the window only asks where it should land.
final class PublishWindow: NSWindowController {
    private let settings: Settings
    private let guide: Guide
    private let destination: PublishDestination

    private let titleField = NSTextField()
    private let first = NSPopUpButton()
    private let second = NSPopUpButton()
    private let third = NSPopUpButton()
    private let formatChoice = NSPopUpButton()
    private let result = NSTextField(wrappingLabelWithString: "")
    private let sendButton = NSButton()

    private var hudu: HuduClient?
    private var confluence: ConfluenceClient?
    private var firstTargets: [PublishTarget] = []
    private var secondTargets: [PublishTarget] = []
    private var thirdTargets: [PublishTarget] = []

    init(settings: Settings, guide: Guide, destination: PublishDestination) {
        self.settings = settings
        self.guide = guide
        self.destination = destination

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 420),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false)

        window.title = destination == .hudu ? "Send to Hudu" : "Send to Confluence"
        window.center()

        super.init(window: window)
        window.appearance = NSAppearance(named: .darkAqua)

        build()
        Task { @MainActor in await loadTargets() }
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    func showModal() {
        guard let window else { return }
        NSApp.runModal(for: window)
        window.orderOut(nil)
    }

    private func build() {
        guard let window, let content = window.contentView else { return }

        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.background.cgColor

        titleField.stringValue = guide.Title

        for box in [first, second, third, formatChoice] {
            box.translatesAutoresizingMaskIntoConstraints = false
        }

        first.target = self
        first.action = #selector(parentChanged)

        for profile in FormatProfiles.all() {
            formatChoice.addItem(withTitle: profile.Name)
        }

        let wanted = destination == .hudu ? settings.huduFormat : settings.confluenceFormat
        if formatChoice.itemTitles.contains(wanted) {
            formatChoice.selectItem(withTitle: wanted)
        }

        result.font = .systemFont(ofSize: 11.5)
        result.textColor = Theme.muted
        result.maximumNumberOfLines = 3

        var views: [NSView] = [
            caption("Title of the article"), titleField,
            caption(destination == .hudu ? "Company" : "Space"), first,
            caption(destination == .hudu ? "Folder" : "File it under"), second,
        ]

        if destination == .hudu {
            views.append(caption("Article"))
            views.append(third)
        }

        views.append(caption("Written using this format"))
        views.append(formatChoice)

        views.append(note(destination == .hudu
            ? "Hudu keeps the pictures inside the article, so this goes across in one piece."
            : "Confluence keeps pictures as attachments, so the page is created first and each picture is attached to it afterwards."))

        views.append(result)

        sendButton.title = "Send"
        sendButton.bezelStyle = .rounded
        sendButton.keyEquivalent = "\r"
        sendButton.bezelColor = Theme.accent
        sendButton.target = self
        sendButton.action = #selector(sendTapped)
        sendButton.isEnabled = false

        let reload = NSButton(title: "Reload the list", target: self, action: #selector(reloadTapped))
        reload.bezelStyle = .rounded

        let close = NSButton(title: "Close", target: self, action: #selector(closeTapped))
        close.bezelStyle = .rounded

        let buttons = NSStackView(views: [reload, NSView(), close, sendButton])
        buttons.orientation = .horizontal
        buttons.spacing = 8
        views.append(buttons)

        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 5
        stack.edgeInsets = NSEdgeInsets(top: 18, left: 20, bottom: 18, right: 20)
        stack.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            titleField.widthAnchor.constraint(equalToConstant: 500),
            first.widthAnchor.constraint(equalToConstant: 500),
            second.widthAnchor.constraint(equalToConstant: 500),
            third.widthAnchor.constraint(equalToConstant: 500),
            formatChoice.widthAnchor.constraint(equalToConstant: 500),
            result.widthAnchor.constraint(equalToConstant: 500),
            buttons.widthAnchor.constraint(equalToConstant: 500),
        ])
    }

    private func caption(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 10.5)
        label.textColor = Theme.muted
        return label
    }

    private func note(_ text: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = .systemFont(ofSize: 10.5)
        label.textColor = Theme.muted
        label.widthAnchor.constraint(equalToConstant: 500).isActive = true
        return label
    }

    // ------------------------------------------------------------------ loading

    @MainActor
    private func loadTargets() async {
        sendButton.isEnabled = false
        say("Asking the site what is there...", Theme.muted)

        do {
            if destination == .hudu {
                guard settings.hasHudu else {
                    say("Hudu is not set up yet. Add the address and a key under Settings.", NSColor.systemRed)
                    return
                }

                let client = try HuduClient(site: settings.huduSite, key: settings.huduKey)
                hudu = client
                firstTargets = try await client.companies()
            } else {
                guard settings.hasConfluence else {
                    say("Confluence is not set up yet. Add the address, your email and a token under Settings.", NSColor.systemRed)
                    return
                }

                let client = try ConfluenceClient(
                    site: settings.confluenceSite,
                    email: settings.confluenceEmail,
                    token: settings.confluenceToken)

                confluence = client
                firstTargets = try await client.spaces()
            }

            fill(first, firstTargets)
            await loadChildren()

            sendButton.isEnabled = true
            say("Ready to send.", NSColor.systemGreen)
        } catch {
            say(error.localizedDescription, NSColor.systemRed)
        }
    }

    @MainActor
    private func loadChildren() async {
        let parent = chosen(first, firstTargets)?.id ?? ""

        do {
            if destination == .hudu, let client = hudu {
                secondTargets = try await client.folders(company: parent)
                thirdTargets = try await client.articles(company: parent)
                fill(second, secondTargets)
                fill(third, thirdTargets)
            } else if let client = confluence {
                secondTargets = try await client.pages(space: parent)
                fill(second, secondTargets)
            }
        } catch {
            say(error.localizedDescription, NSColor.systemRed)
        }
    }

    // ------------------------------------------------------------------ sending

    @MainActor
    private func send() async {
        let title = titleField.stringValue.trimmingCharacters(in: .whitespaces)

        guard !title.isEmpty else {
            say("The article needs a title.", NSColor.systemRed)
            return
        }

        sendButton.isEnabled = false

        do {
            let format = FormatProfiles.find(formatChoice.titleOfSelectedItem)

            var options = HtmlOptions()
            options.fragment = true
            options.format = format
            options.embedImages = destination == .hudu
            options.collectImagesOnly = destination == .confluence

            let collected = CollectedImages()

            say("Building the document...", Theme.muted)
            let html = HtmlExporter.build(
                guide: guide,
                settings: settings,
                options: options,
                collected: collected)

            var link = ""

            if destination == .hudu, let client = hudu {
                say("Sending to Hudu...", Theme.muted)

                link = try await client.publish(
                    title: title,
                    html: html,
                    company: chosen(first, firstTargets)?.id ?? "",
                    folder: chosen(second, secondTargets)?.id ?? "",
                    article: chosen(third, thirdTargets)?.id ?? "")

                settings.huduFormat = format.Name
            } else if let client = confluence {
                let space = chosen(first, firstTargets)?.id ?? ""

                guard !space.isEmpty else {
                    say("Choose a space first.", NSColor.systemRed)
                    sendButton.isEnabled = true
                    return
                }

                link = try await client.publish(
                    title: title,
                    storage: html,
                    space: space,
                    parent: chosen(second, secondTargets)?.id ?? "",
                    pictures: collected.pictures,
                    jpeg: format.UseJpeg,
                    progress: { [weak self] message in
                        Task { @MainActor in self?.say(message, Theme.muted) }
                    })

                settings.confluenceFormat = format.Name
            }

            say("Sent. " + link, NSColor.systemGreen)

            let alert = NSAlert()
            alert.messageText = "The article is published"
            alert.informativeText = "Open it now?"
            alert.addButton(withTitle: "Open")
            alert.addButton(withTitle: "Not now")

            if alert.runModal() == .alertFirstButtonReturn, let url = URL(string: link) {
                NSWorkspace.shared.open(url)
            }
        } catch {
            say(error.localizedDescription, NSColor.systemRed)
        }

        sendButton.isEnabled = true
    }

    // ------------------------------------------------------------------ helpers

    private func fill(_ box: NSPopUpButton, _ targets: [PublishTarget]) {
        box.removeAllItems()
        for target in targets {
            box.addItem(withTitle: target.label)
        }
    }

    private func chosen(_ box: NSPopUpButton, _ targets: [PublishTarget]) -> PublishTarget? {
        let index = box.indexOfSelectedItem
        return index >= 0 && index < targets.count ? targets[index] : nil
    }

    private func say(_ message: String, _ color: NSColor) {
        result.stringValue = message
        result.textColor = color
    }

    @objc private func parentChanged() {
        Task { @MainActor in await loadChildren() }
    }

    @objc private func reloadTapped() {
        Task { @MainActor in await loadTargets() }
    }

    @objc private func sendTapped() {
        Task { @MainActor in await send() }
    }

    @objc private func closeTapped() {
        NSApp.stopModal()
    }
}
