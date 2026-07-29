import AppKit
import Foundation

/// Shows what macOS still has to allow, updating itself as you grant each one.
///
/// It exists because the usual one line alert is misleading here. Two of these permissions
/// are read once when the process starts, so granting one while the app is open changes
/// nothing until it is opened again, and an app run from a download is given a throwaway
/// path that loses every grant. Both are handled in plain sight rather than left to puzzle
/// over.
final class PermissionsWindow: NSWindowController {
    private var rows: [Permissions.Kind: (status: NSTextField, button: NSButton)] = [:]
    private let locationBox = NSView()
    private let locationLabel = NSTextField(wrappingLabelWithString: "")
    private let moveButton = NSButton()
    private let reopenButton = NSButton()
    private var poll: Timer?

    var onFinished: (() -> Void)?

    init() {
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 560, height: 470),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false)

        window.title = "Stepwright needs permission"
        window.center()

        super.init(window: window)
        window.appearance = NSAppearance(named: .darkAqua)
        build()
        refresh()

        poll = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.refresh()
        }
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    deinit { poll?.invalidate() }

    private func build() {
        guard let window, let content = window.contentView else { return }

        content.wantsLayer = true
        content.layer?.backgroundColor = Theme.background.cgColor

        let heading = NSTextField(labelWithString: "Three things to allow")
        heading.font = .systemFont(ofSize: 17, weight: .semibold)
        heading.textColor = Theme.text

        let blurb = NSTextField(wrappingLabelWithString:
            "macOS keeps these behind a switch you have to turn on yourself. "
            + "Stepwright cannot record until all three are on.")
        blurb.font = .systemFont(ofSize: 12)
        blurb.textColor = Theme.muted

        var views: [NSView] = [heading, blurb, spacer(8)]

        for kind in Permissions.Kind.allCases {
            views.append(row(for: kind))
        }

        views.append(spacer(10))
        views.append(buildLocationBox())
        views.append(spacer(6))

        reopenButton.title = "Quit and open again"
        reopenButton.bezelStyle = .rounded
        reopenButton.target = self
        reopenButton.action = #selector(reopenTapped)

        let close = NSButton(title: "Continue", target: self, action: #selector(closeTapped))
        close.bezelStyle = .rounded
        close.keyEquivalent = "\r"

        let footer = NSStackView(views: [reopenButton, NSView(), close])
        footer.orientation = .horizontal
        footer.spacing = 8
        views.append(footer)

        let stack = NSStackView(views: views)
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 8
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 22, bottom: 20, right: 22)
        stack.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            blurb.widthAnchor.constraint(equalToConstant: 500),
            footer.widthAnchor.constraint(equalToConstant: 500),
        ])
    }

    private func row(for kind: Permissions.Kind) -> NSView {
        let status = NSTextField(labelWithString: "")
        status.font = .systemFont(ofSize: 15)
        status.alignment = .center

        let title = NSTextField(labelWithString: kind.title)
        title.font = .systemFont(ofSize: 13, weight: .semibold)
        title.textColor = Theme.text

        let reason = NSTextField(wrappingLabelWithString: kind.reason)
        reason.font = .systemFont(ofSize: 11)
        reason.textColor = Theme.muted

        let button = NSButton(title: "Allow", target: self, action: #selector(allowTapped(_:)))
        button.bezelStyle = .rounded
        button.tag = index(of: kind)

        let text = NSStackView(views: [title, reason])
        text.orientation = .vertical
        text.alignment = .leading
        text.spacing = 1

        let line = NSStackView(views: [status, text, NSView(), button])
        line.orientation = .horizontal
        line.alignment = .centerY
        line.spacing = 10
        line.edgeInsets = NSEdgeInsets(top: 8, left: 10, bottom: 8, right: 10)
        line.wantsLayer = true
        line.layer?.backgroundColor = Theme.panel.cgColor
        line.layer?.cornerRadius = 8

        NSLayoutConstraint.activate([
            status.widthAnchor.constraint(equalToConstant: 22),
            reason.widthAnchor.constraint(equalToConstant: 330),
            line.widthAnchor.constraint(equalToConstant: 500),
        ])

        rows[kind] = (status, button)
        return line
    }

    private func buildLocationBox() -> NSView {
        locationLabel.font = .systemFont(ofSize: 11.5)
        locationLabel.textColor = Theme.text

        moveButton.title = "Move to Applications"
        moveButton.bezelStyle = .rounded
        moveButton.target = self
        moveButton.action = #selector(moveTapped)

        let line = NSStackView(views: [locationLabel, NSView(), moveButton])
        line.orientation = .horizontal
        line.alignment = .centerY
        line.spacing = 10
        line.edgeInsets = NSEdgeInsets(top: 10, left: 10, bottom: 10, right: 10)
        line.wantsLayer = true
        line.layer?.backgroundColor = Theme.panel.cgColor
        line.layer?.cornerRadius = 8

        NSLayoutConstraint.activate([
            locationLabel.widthAnchor.constraint(equalToConstant: 330),
            line.widthAnchor.constraint(equalToConstant: 500),
        ])

        locationBox.addSubview(line)
        line.translatesAutoresizingMaskIntoConstraints = false

        NSLayoutConstraint.activate([
            line.leadingAnchor.constraint(equalTo: locationBox.leadingAnchor),
            line.trailingAnchor.constraint(equalTo: locationBox.trailingAnchor),
            line.topAnchor.constraint(equalTo: locationBox.topAnchor),
            line.bottomAnchor.constraint(equalTo: locationBox.bottomAnchor),
        ])

        return locationBox
    }

    private func spacer(_ height: CGFloat) -> NSView {
        let view = NSView()
        view.heightAnchor.constraint(equalToConstant: height).isActive = true
        return view
    }

    private func index(of kind: Permissions.Kind) -> Int {
        Permissions.Kind.allCases.firstIndex(of: kind) ?? 0
    }

    private func kind(at tag: Int) -> Permissions.Kind {
        let all = Permissions.Kind.allCases
        return all[max(0, min(tag, all.count - 1))]
    }

    func refresh() {
        for kind in Permissions.Kind.allCases {
            guard let row = rows[kind] else { continue }
            let granted = Permissions.granted(kind)

            row.status.stringValue = granted ? "\u{2713}" : "\u{25CB}"
            row.status.textColor = granted ? NSColor.systemGreen : Theme.muted
            row.button.title = granted ? "Allowed" : "Allow"
            row.button.isEnabled = !granted
        }

        // The place the app is running from decides whether any of this survives a restart.
        if Permissions.isRelocated {
            locationLabel.stringValue = "macOS is running this copy from a temporary place. "
                + "Permissions granted here are forgotten every time. Drag Stepwright to your "
                + "Applications folder and open it from there."
            locationLabel.textColor = NSColor.systemOrange
            moveButton.isHidden = true
            locationBox.isHidden = false
        } else if !Permissions.isInApplications {
            locationLabel.stringValue = "Stepwright is not in your Applications folder. "
                + "Moving it there is what makes these permissions stay granted."
            locationLabel.textColor = Theme.text
            moveButton.isHidden = false
            locationBox.isHidden = false
        } else {
            locationBox.isHidden = true
        }

        let waiting = Permissions.missing.contains { $0.needsReopen }
        reopenButton.isHidden = !waiting
        reopenButton.title = "Quit and open again"
    }

    @objc private func allowTapped(_ sender: NSButton) {
        let kind = self.kind(at: sender.tag)
        Permissions.request(kind)

        if kind.needsReopen {
            let alert = NSAlert()
            alert.messageText = "Turn on \(kind.title), then open Stepwright again"
            alert.informativeText = """
            macOS only reads this permission when an app starts, so it will not take effect \
            until Stepwright has been opened again. Turn the switch on in the window that \
            just appeared, then use Quit and open again.
            """

            alert.addButton(withTitle: "Fine")
            alert.runModal()
        }
    }

    @objc private func moveTapped() {
        if let problem = Permissions.moveToApplications() {
            let alert = NSAlert()
            alert.messageText = "Move Stepwright yourself"
            alert.informativeText = problem
            alert.runModal()
        }
    }

    @objc private func reopenTapped() {
        Permissions.relaunch()
    }

    @objc private func closeTapped() {
        poll?.invalidate()
        poll = nil
        window?.close()
        onFinished?()
    }
}
