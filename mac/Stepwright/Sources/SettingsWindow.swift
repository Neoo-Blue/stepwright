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
    private let aiAuth = NSPopUpButton()
    private let aiBaseUrl = NSTextField()
    private let aiModel = NSComboBox()
    private let aiKey = NSSecureTextField()
    private let aiToken = NSSecureTextField()
    private let aiCliPath = NSTextField()
    private let aiCliStatus = NSTextField(wrappingLabelWithString: "")

    /// Which route each identifier in the sign in list stands for.
    private var authKinds: [String] = []

    /// The fields that belong to one route, hidden as a group when another is chosen.
    private var keyViews: [NSView] = []
    private var cliViews: [NSView] = []
    private var tokenViews: [NSView] = []
    private var tokenEdited = false
    private let aiPictures = NSButton(checkboxWithTitle: "Let the assistant see each screenshot", target: nil, action: nil)
    private let aiNotes = NSButton(checkboxWithTitle: "Write a note under a step when it helps", target: nil, action: nil)
    private let aiResult = NSTextField(labelWithString: "")
    private let aiHint = NSTextField(labelWithString: "")

    private var keyEdited = false

    private let exportFormat = NSPopUpButton()
    private let formatDetail = NSTextField(wrappingLabelWithString: "")

    private let huduSite = NSTextField()
    private let huduKey = NSSecureTextField()
    private let huduResult = NSTextField(wrappingLabelWithString: "")
    private let confluenceAuth = NSPopUpButton()
    private let confluenceSite = NSTextField()
    private let confluenceEmail = NSTextField()
    private let confluenceToken = NSSecureTextField()
    private let confluenceClientId = NSTextField()
    private let confluenceSecret = NSSecureTextField()
    private let confluenceSignedIn = NSTextField(wrappingLabelWithString: "")
    private let confluenceResult = NSTextField(wrappingLabelWithString: "")

    /// The fields that belong to one route, hidden as a group when the other is chosen.
    private var confluenceTokenViews: [NSView] = []
    private var confluenceOAuthViews: [NSView] = []

    private var huduKeyEdited = false
    private var confluenceTokenEdited = false
    private var confluenceSecretEdited = false

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

        for profile in FormatProfiles.all() {
            exportFormat.addItem(withTitle: profile.Name)
        }

        if exportFormat.itemTitles.contains(settings.exportFormat) {
            exportFormat.selectItem(withTitle: settings.exportFormat)
        }

        exportFormat.target = self
        exportFormat.action = #selector(formatChanged)

        formatDetail.font = .systemFont(ofSize: 10.5)
        formatDetail.textColor = Theme.muted
        formatDetail.maximumNumberOfLines = 3

        let formatButtons = NSStackView(views: [
            NSButton(title: "Import", target: self, action: #selector(importFormatTapped)),
            NSButton(title: "Export this one", target: self, action: #selector(exportFormatTapped)),
            NSButton(title: "Duplicate and edit", target: self, action: #selector(duplicateFormatTapped)),
            NSButton(title: "Open the folder", target: self, action: #selector(openFormatFolderTapped)),
            NSButton(title: "Delete", target: self, action: #selector(deleteFormatTapped)),
        ])

        formatButtons.orientation = .horizontal
        formatButtons.spacing = 6

        tabs.addTabViewItem(page("Format", views: [
            note("A format decides how a guide is written out: the typeface, the sizes, whether the"
                 + " styling travels on each element, and how pictures are carried. Every export and"
                 + " every publish uses one, and a format is a small file you can share."),
            caption("Format used when exporting"), exportFormat,
            formatDetail,
            formatButtons,
            note("The four that ship with the app cannot be deleted. Duplicate one to make your own,"
                 + " which opens the folder so you can edit the file."),
        ]))

        huduSite.stringValue = settings.huduSite
        huduKey.stringValue = settings.huduKey.isEmpty ? "" : String(repeating: "*", count: 24)
        confluenceSite.stringValue = settings.confluenceSite
        confluenceEmail.stringValue = settings.confluenceEmail
        confluenceToken.stringValue = settings.confluenceToken.isEmpty ? "" : String(repeating: "*", count: 24)
        confluenceClientId.stringValue = settings.confluenceClientId
        confluenceSecret.stringValue = settings.confluenceSecret.isEmpty ? "" : String(repeating: "*", count: 24)

        confluenceAuth.addItems(withTitles: [
            "An email address and an API token",
            "Sign in through the browser, with your own Atlassian application",
        ])

        confluenceAuth.selectItem(at: settings.confluenceUsesOAuth ? 1 : 0)
        confluenceAuth.target = self
        confluenceAuth.action = #selector(confluenceAuthChanged)

        confluenceTokenViews = [
            caption("The email address you sign in with"), confluenceEmail,
            caption("API token, from your Atlassian account security page"), confluenceToken,
            row([NSButton(title: "Open the token page", target: self, action: #selector(atlassianTokensTapped))]),
        ]

        confluenceSignedIn.font = .systemFont(ofSize: 10.5)

        confluenceOAuthViews = [
            note("Register an application once in the Atlassian developer console, give it the"
                 + " Confluence permissions, and add " + Atlassian.callbackUrl
                 + " as its callback address. Then sign in here and nothing has to be pasted again."),
            caption("Application identifier"), confluenceClientId,
            caption("Application secret, kept in your keychain"), confluenceSecret,
            row([
                NSButton(title: "Sign in to Atlassian", target: self, action: #selector(signInConfluenceTapped)),
                NSButton(title: "Sign out", target: self, action: #selector(signOutConfluenceTapped)),
                NSButton(title: "Open the console", target: self, action: #selector(consoleTapped)),
            ]),
            confluenceSignedIn,
        ]

        for result in [huduResult, confluenceResult] {
            result.font = .systemFont(ofSize: 11)
            result.textColor = Theme.muted
            result.maximumNumberOfLines = 2
        }

        tabs.addTabViewItem(page("Publishing", views: [
            note("Sends a finished guide straight into a knowledge base, with no file in between."
                 + " Every secret here is kept in your keychain."),
            heading("Hudu"),
            caption("Address of your site"), huduSite,
            caption("API key, from Admin then API in Hudu"), huduKey,
            row([
                NSButton(title: "Test the connection", target: self, action: #selector(testHuduTapped)),
                NSButton(title: "Open the key page", target: self, action: #selector(huduKeysTapped)),
            ]),
            huduResult,
            heading("Confluence"),
            caption("How Stepwright signs in"), confluenceAuth,
            caption("Address of your site"), confluenceSite,
        ] + confluenceTokenViews + confluenceOAuthViews + [
            NSButton(title: "Test the connection", target: self, action: #selector(testConfluenceTapped)),
            confluenceResult,
        ]))

        huduKey.target = self
        confluenceToken.target = self

        showFormatDetail()

        keyViews = [
            caption("Address"), aiBaseUrl,
            caption("Key, kept in your keychain"), aiKey,
            row([aiHint, keyLink]),
        ]

        cliViews = [
            note("Stepwright runs the app on this machine and reads what it says back. No token"
                 + " is kept here, and nothing is billed by the token."),
            aiCliStatus,
            row([
                NSButton(title: "Check the app", target: self, action: #selector(checkAgentTapped)),
                NSButton(title: "Sign in", target: self, action: #selector(signInAgentTapped)),
                NSButton(title: "How to install it", target: self, action: #selector(agentPageTapped)),
            ]),
            caption("Where the app is, only when it lives somewhere unusual"), aiCliPath,
        ]

        tokenViews = [
            note("Advanced. A subscription token is issued for the vendor's own app, and sending"
                 + " it from anything else is outside the terms of a consumer plan. Accounts have"
                 + " been suspended for it. The safe route is the app above, or a key."),
            caption("Token, kept in your keychain"), aiToken,
            row([NSButton(title: "Make a token for me", target: self, action: #selector(makeTokenTapped))]),
            note("That opens Terminal running claude setup-token, which signs you in through your"
                 + " browser and then prints a token. Copy it and paste it above."),
        ]

        aiAuth.target = self
        aiAuth.action = #selector(authChanged)

        tabs.addTabViewItem(page("Assistant", views: [
            aiEnabled,
            caption("Service"), aiProvider,
            caption("How it signs in"), aiAuth,
        ] + keyViews + cliViews + tokenViews + [
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
        aiToken.stringValue = settings.hasAiToken ? String(repeating: "*", count: 24) : ""
        aiCliPath.stringValue = settings.aiCliPath
        aiPictures.state = settings.aiSendScreenshots ? .on : .off
        aiNotes.state = settings.aiWriteNotes ? .on : .off

        if let index = AiProviders.all.firstIndex(where: { $0.id == settings.aiProvider }) {
            aiProvider.selectItem(at: index)
        }

        reloadAuthChoices(settings.aiAuth)
        showAuthRoute()
        showConfluenceRoute()
        showHint()
    }

    /// Offers only the routes the chosen service actually has.
    private func reloadAuthChoices(_ wanted: String) {
        let keep = AiAuthKinds.clean(wanted)

        authKinds = [AiAuthKinds.key]
        aiAuth.removeAllItems()
        aiAuth.addItem(withTitle: "A key I bought, billed by what it uses")

        if let agent = AiAgents.find(chosenProvider.id) {
            authKinds.append(AiAuthKinds.cli)
            aiAuth.addItem(withTitle: "\(agent.label) on this machine, paid by my \(agent.plan) plan")
        }

        if chosenProvider.id == "anthropic" {
            authKinds.append(AiAuthKinds.token)
            aiAuth.addItem(withTitle: "A Claude subscription token, advanced")
        }

        aiAuth.selectItem(at: authKinds.firstIndex(of: keep) ?? 0)
        showAuthRoute()
    }

    private var chosenAuth: String {
        let index = aiAuth.indexOfSelectedItem
        return index >= 0 && index < authKinds.count ? authKinds[index] : AiAuthKinds.key
    }

    /// Shows the fields the chosen route needs and hides the rest.
    private func showAuthRoute() {
        let auth = chosenAuth

        for view in keyViews { view.isHidden = auth != AiAuthKinds.key }
        for view in cliViews { view.isHidden = auth != AiAuthKinds.cli }
        for view in tokenViews { view.isHidden = auth != AiAuthKinds.token }

        if auth == AiAuthKinds.cli, let agent = AiAgents.find(chosenProvider.id) {
            let found = AiAgents.locate(agent, saved: aiCliPath.stringValue)

            aiCliStatus.textColor = found == nil ? Theme.muted : Theme.accent
            aiCliStatus.stringValue = found == nil
                ? "\(agent.label) was not found on this machine. \(agent.signIn)"
                : "Found \(agent.label) at \(found!). \(agent.signIn)"
        }

        if auth == AiAuthKinds.token {
            aiBaseUrl.stringValue = AiProviders.find("anthropic").baseUrl
        }
    }

    @objc private func authChanged() { showAuthRoute() }

    @objc private func checkAgentTapped() {
        guard let agent = AiAgents.find(chosenProvider.id) else { return }

        aiCliStatus.textColor = Theme.muted
        aiCliStatus.stringValue = "Looking for \(agent.label)..."

        do {
            let version = try AiAgents.version(agent, saved: aiCliPath.stringValue)
            aiCliStatus.textColor = Theme.accent
            aiCliStatus.stringValue = "\(agent.label) answered: \(version). \(agent.signIn)"
        } catch {
            aiCliStatus.textColor = .systemRed
            aiCliStatus.stringValue = error.localizedDescription
        }
    }

    @objc private func agentPageTapped() {
        guard let agent = AiAgents.find(chosenProvider.id),
              let url = URL(string: agent.installPage) else { return }

        NSWorkspace.shared.open(url)
    }

    @objc private func signInAgentTapped() {
        guard let agent = AiAgents.find(chosenProvider.id) else { return }

        runInTerminal(
            agent.signInCommand,
            what: "Terminal is running \(agent.signInCommand). Sign in there with your"
                + " \(agent.plan) account, then come back and press Check the app.")
    }

    /// Claude Code is the only thing that can mint one of these, so the button runs it rather
    /// than sending the person off to find the instructions.
    @objc private func makeTokenTapped() {
        runInTerminal(
            "claude setup-token",
            what: "Terminal is running claude setup-token. Sign in when the browser asks, then"
                + " copy the token it prints and paste it into the token box.")
    }

    /// Opens Terminal with the command already running. These sign ins open a browser and then
    /// print something worth reading, so a window that stays is the point.
    private func runInTerminal(_ command: String, what: String) {
        let path = NSTemporaryDirectory() + "stepwright-signin.command"

        let script = """
        #!/bin/sh
        \(command)
        """

        do {
            try script.write(toFile: path, atomically: true, encoding: .utf8)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: path)
        } catch {
            aiCliStatus.textColor = .systemRed
            aiCliStatus.stringValue = "Terminal could not be opened. Run \(command) yourself."
            return
        }

        NSWorkspace.shared.open(URL(fileURLWithPath: path))

        let alert = NSAlert()
        alert.messageText = "Stepwright"
        alert.informativeText = what
        alert.runModal()
    }

    private var chosenProvider: AiProvider {
        AiProviders.all[max(0, min(aiProvider.indexOfSelectedItem, AiProviders.all.count - 1))]
    }

    private func showHint() { aiHint.stringValue = chosenProvider.hint }

    /// The values the buttons on this page should be tried against.
    private func probe() -> Settings {
        let probe = Settings()
        probe.aiProvider = chosenProvider.id
        probe.aiAuth = chosenAuth
        probe.aiBaseUrl = aiBaseUrl.stringValue.trimmingCharacters(in: .whitespaces)
        probe.aiModel = aiModel.stringValue.trimmingCharacters(in: .whitespaces)
        probe.aiCliPath = aiCliPath.stringValue.trimmingCharacters(in: .whitespaces)

        if keyEdited, !aiKey.stringValue.isEmpty {
            probe.aiKey = aiKey.stringValue.trimmingCharacters(in: .whitespaces)
        }

        if tokenEdited, !aiToken.stringValue.isEmpty {
            probe.aiToken = aiToken.stringValue.trimmingCharacters(in: .whitespaces)
        }

        return probe
    }

    @objc private func providerChanged() {
        aiBaseUrl.stringValue = chosenProvider.baseUrl
        aiModel.removeAllItems()
        aiModel.stringValue = chosenProvider.model
        reloadAuthChoices(chosenAuth)
        showHint()
    }

    @objc private func keyPageTapped() {
        guard !chosenProvider.keyPage.isEmpty, let url = URL(string: chosenProvider.keyPage) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func findModelsTapped() {
        keyEdited = keyEdited || !aiKey.stringValue.contains("*")
        tokenEdited = tokenEdited || !aiToken.stringValue.contains("*")
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
        tokenEdited = tokenEdited || !aiToken.stringValue.contains("*")
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

    private func note(_ text: String) -> NSTextField {
        let label = NSTextField(wrappingLabelWithString: text)
        label.font = .systemFont(ofSize: 10.5)
        label.textColor = Theme.muted
        label.widthAnchor.constraint(equalToConstant: 520).isActive = true
        return label
    }

    private func heading(_ text: String) -> NSTextField {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: 13, weight: .semibold)
        label.textColor = Theme.text
        return label
    }

    private var chosenFormat: FormatProfile {
        FormatProfiles.find(exportFormat.titleOfSelectedItem)
    }

    private func reloadFormats(_ chosen: String?) {
        exportFormat.removeAllItems()

        for profile in FormatProfiles.all() {
            exportFormat.addItem(withTitle: profile.Name)
        }

        if let chosen, exportFormat.itemTitles.contains(chosen) {
            exportFormat.selectItem(withTitle: chosen)
        }

        showFormatDetail()
    }

    private func showFormatDetail() {
        let profile = chosenFormat
        let kind = profile.isBuiltIn ? "Ships with the app" : "Yours, saved on this machine"
        formatDetail.stringValue = profile.Description + "\n" + kind
    }

    @objc private func formatChanged() { showFormatDetail() }

    @objc private func importFormatTapped() {
        let panel = NSOpenPanel()
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false

        guard panel.runModal() == .OK, let url = panel.url else { return }

        guard let loaded = FormatProfiles.load(url) else {
            let alert = NSAlert()
            alert.messageText = "That file is not a format Stepwright understands."
            alert.runModal()
            return
        }

        FormatProfiles.save(loaded)
        reloadFormats(loaded.Name)
    }

    @objc private func exportFormatTapped() {
        let profile = chosenFormat
        let panel = NSSavePanel()
        panel.nameFieldStringValue = profile.Name + "." + FormatProfiles.fileExtension

        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? FormatProfiles.write(profile, to: url)
    }

    @objc private func duplicateFormatTapped() {
        var copy = chosenFormat
        copy.Name += " copy"
        copy.Description = "Your own version."
        copy.isBuiltIn = false

        FormatProfiles.save(copy)
        reloadFormats(copy.Name)
        openFormatFolderTapped()
    }

    @objc private func openFormatFolderTapped() {
        try? FileManager.default.createDirectory(at: FormatProfiles.folder, withIntermediateDirectories: true)
        NSWorkspace.shared.open(FormatProfiles.folder)
    }

    @objc private func deleteFormatTapped() {
        let profile = chosenFormat

        guard !profile.isBuiltIn else {
            let alert = NSAlert()
            alert.messageText = "The formats that ship with the app cannot be deleted."
            alert.runModal()
            return
        }

        FormatProfiles.delete(profile.Name)
        reloadFormats(nil)
    }

    @objc private func testHuduTapped() {
        huduKeyEdited = huduKeyEdited || !huduKey.stringValue.contains("*")
        huduResult.stringValue = "Talking to Hudu..."
        huduResult.textColor = Theme.muted

        let site = huduSite.stringValue
        let key = huduKeyEdited ? huduKey.stringValue : settings.huduKey

        Task { @MainActor in
            do {
                let client = try HuduClient(site: site, key: key)
                self.huduResult.stringValue = try await client.check()
                self.huduResult.textColor = NSColor.systemGreen
            } catch {
                self.huduResult.stringValue = error.localizedDescription
                self.huduResult.textColor = NSColor.systemRed
            }
        }
    }

    @objc private func testConfluenceTapped() {
        confluenceTokenEdited = confluenceTokenEdited || !confluenceToken.stringValue.contains("*")
        confluenceResult.stringValue = "Talking to Confluence..."
        confluenceResult.textColor = Theme.muted

        let site = confluenceSite.stringValue
        let email = confluenceEmail.stringValue
        let token = confluenceTokenEdited ? confluenceToken.stringValue : settings.confluenceToken
        let oauth = confluenceAuth.indexOfSelectedItem == 1

        Task { @MainActor in
            do {
                let client = oauth
                    ? try await ConfluenceClient.make(settings: self.settings)
                    : try ConfluenceClient(site: site, email: email, token: token)

                self.confluenceResult.stringValue = try await client.check()
                self.confluenceResult.textColor = NSColor.systemGreen
            } catch {
                self.confluenceResult.stringValue = error.localizedDescription
                self.confluenceResult.textColor = NSColor.systemRed
            }
        }
    }

    private func showConfluenceRoute() {
        let oauth = confluenceAuth.indexOfSelectedItem == 1

        for view in confluenceTokenViews { view.isHidden = oauth }
        for view in confluenceOAuthViews { view.isHidden = !oauth }

        showConfluenceSignIn()
    }

    private func showConfluenceSignIn() {
        confluenceSignedIn.textColor = settings.hasConfluenceSignIn ? NSColor.systemGreen : Theme.muted
        confluenceSignedIn.stringValue = settings.hasConfluenceSignIn
            ? "Signed in to \(settings.confluenceSiteName). Stepwright renews this on its own."
            : "Not signed in yet."
    }

    @objc private func confluenceAuthChanged() { showConfluenceRoute() }

    @objc private func consoleTapped() {
        guard let url = URL(string: Atlassian.consolePage) else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func atlassianTokensTapped() {
        guard let url = URL(string: "https://id.atlassian.com/manage-profile/security/api-tokens") else { return }
        NSWorkspace.shared.open(url)
    }

    /// Hudu keeps its keys under the admin area of your own site, so the address is built from
    /// the one already filled in rather than being somewhere on the internet.
    @objc private func huduKeysTapped() {
        var site = huduSite.stringValue.trimmingCharacters(in: .whitespaces)
        while site.hasSuffix("/") { site.removeLast() }

        guard !site.isEmpty else {
            huduResult.textColor = Theme.muted
            huduResult.stringValue = "Fill in the address of your Hudu site first."
            return
        }

        if !site.lowercased().hasPrefix("http") { site = "https://" + site }

        guard let url = URL(string: site + "/admin/api_keys") else { return }
        NSWorkspace.shared.open(url)
    }

    @objc private func signInConfluenceTapped() {
        confluenceSecretEdited = confluenceSecretEdited || !confluenceSecret.stringValue.contains("*")

        let id = confluenceClientId.stringValue.trimmingCharacters(in: .whitespaces)
        let secret = confluenceSecretEdited
            ? confluenceSecret.stringValue.trimmingCharacters(in: .whitespaces)
            : settings.confluenceSecret

        confluenceResult.textColor = Theme.muted
        confluenceResult.stringValue = "Opening the browser..."

        Task { @MainActor in
            do {
                let session = try await Atlassian.signIn(
                    clientId: id,
                    clientSecret: secret,
                    progress: { message in
                        Task { @MainActor in self.confluenceResult.stringValue = message }
                    })

                // Kept straight away, because a sign in lost by pressing Cancel is worse than
                // one kept by mistake.
                self.settings.confluenceAuth = "oauth"
                self.settings.confluenceClientId = id
                self.settings.confluenceSecret = secret
                self.settings.rememberConfluence(session)

                self.confluenceSite.stringValue = self.settings.confluenceSite
                self.showConfluenceSignIn()

                self.confluenceResult.textColor = NSColor.systemGreen
                self.confluenceResult.stringValue = "Signed in to " + session.siteName + "."
            } catch {
                self.confluenceResult.textColor = NSColor.systemRed
                self.confluenceResult.stringValue = error.localizedDescription
            }
        }
    }

    @objc private func signOutConfluenceTapped() {
        settings.forgetConfluence()
        showConfluenceSignIn()

        confluenceResult.textColor = Theme.muted
        confluenceResult.stringValue = "Signed out. The application details are kept for next time."
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
        settings.aiAuth = chosenAuth
        settings.aiBaseUrl = aiBaseUrl.stringValue.trimmingCharacters(in: .whitespaces)
        settings.aiModel = aiModel.stringValue.trimmingCharacters(in: .whitespaces)
        settings.aiCliPath = aiCliPath.stringValue.trimmingCharacters(in: .whitespaces)
        settings.aiSendScreenshots = aiPictures.state == .on
        settings.aiWriteNotes = aiNotes.state == .on

        if keyEdited || !aiKey.stringValue.contains("*") {
            settings.aiKey = aiKey.stringValue.trimmingCharacters(in: .whitespaces)
        }

        if tokenEdited || !aiToken.stringValue.contains("*") {
            settings.aiToken = aiToken.stringValue.trimmingCharacters(in: .whitespaces)
        }

        settings.exportFormat = exportFormat.titleOfSelectedItem ?? "Stepwright"

        settings.huduSite = huduSite.stringValue.trimmingCharacters(in: .whitespaces)
        if huduKeyEdited || !huduKey.stringValue.contains("*") {
            settings.huduKey = huduKey.stringValue.trimmingCharacters(in: .whitespaces)
        }

        settings.confluenceAuth = confluenceAuth.indexOfSelectedItem == 1 ? "oauth" : "token"
        settings.confluenceSite = confluenceSite.stringValue.trimmingCharacters(in: .whitespaces)
        settings.confluenceEmail = confluenceEmail.stringValue.trimmingCharacters(in: .whitespaces)
        settings.confluenceClientId = confluenceClientId.stringValue.trimmingCharacters(in: .whitespaces)

        if confluenceSecretEdited || !confluenceSecret.stringValue.contains("*") {
            settings.confluenceSecret = confluenceSecret.stringValue.trimmingCharacters(in: .whitespaces)
        }

        if confluenceTokenEdited || !confluenceToken.stringValue.contains("*") {
            settings.confluenceToken = confluenceToken.stringValue.trimmingCharacters(in: .whitespaces)
        }

        NSApp.stopModal()
    }

    @objc private func cancelTapped() { NSApp.stopModal() }
}
