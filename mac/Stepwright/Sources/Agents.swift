import Foundation

/// How the assistant proves who it is.
enum AiAuthKinds {
    /// A key bought from the service, billed by the token.
    static let key = "key"

    /// The command line app you already signed in to, paid for by your subscription.
    static let cli = "cli"

    /// A subscription token sent straight to the service. Advanced, and see the warning.
    static let token = "token"

    static func clean(_ value: String) -> String {
        switch value.lowercased() {
        case cli: return cli
        case token: return token
        default: return key
        }
    }
}

/// A command line app that is already signed in to a subscription. Stepwright runs it the way
/// a person would at a prompt, so the work is paid for by the plan the person already has and
/// no token ever passes through this app.
struct AiAgent {
    /// Matches the service identifier in AiProviders.
    let id: String
    let label: String

    /// The name of the command.
    let command: String

    /// What the person types once, in a terminal, to sign in.
    let signIn: String

    /// Which plan pays for it, said plainly for the settings page.
    let plan: String
    let installPage: String

    /// Names worth offering, since there is no service to ask.
    let models: [String]

    /// True when the app takes picture files as arguments rather than being asked to read them.
    let attachesPictures: Bool
}

enum AiAgents {
    static let all: [AiAgent] = [
        AiAgent(
            id: "anthropic",
            label: "Claude Code",
            command: "claude",
            signIn: "Run claude in Terminal once and sign in, or type /login inside it.",
            plan: "Claude Pro or Claude Max",
            installPage: "https://docs.claude.com/en/docs/claude-code/setup",
            models: ["sonnet", "opus", "haiku"],
            attachesPictures: false),
        AiAgent(
            id: "openai",
            label: "Codex",
            command: "codex",
            signIn: "Run codex login in Terminal once and choose Sign in with ChatGPT.",
            plan: "ChatGPT Plus, Pro, Business or Enterprise",
            installPage: "https://developers.openai.com/codex/cli",
            models: ["gpt-5.1-codex", "gpt-5.1"],
            attachesPictures: true),
        AiAgent(
            id: "gemini",
            label: "Gemini CLI",
            command: "gemini",
            signIn: "Run gemini in Terminal once and sign in with your Google account.",
            plan: "A Google account, or a Gemini Code Assist plan",
            installPage: "https://github.com/google-gemini/gemini-cli",
            models: ["gemini-2.5-pro", "gemini-2.5-flash"],
            attachesPictures: false),
    ]

    static func find(_ providerId: String) -> AiAgent? {
        all.first { $0.id.caseInsensitiveCompare(providerId) == .orderedSame }
    }

    // ------------------------------------------------------------------ finding the app

    /// Where the app is on this machine, or nothing when it is not installed. An app started
    /// from the Finder inherits almost no search path, so the usual places are checked by hand
    /// before the login shell is asked as a last resort.
    static func locate(_ agent: AiAgent, saved: String = "") -> String? {
        let manager = FileManager.default

        let trimmed = saved.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty, manager.isExecutableFile(atPath: trimmed) {
            return trimmed
        }

        let home = NSHomeDirectory()
        var folders = [
            "/opt/homebrew/bin",
            "/usr/local/bin",
            home + "/.local/bin",
            home + "/.bun/bin",
            home + "/.npm-global/bin",
            "/usr/bin",
        ]

        if let path = ProcessInfo.processInfo.environment["PATH"] {
            folders.append(contentsOf: path.split(separator: ":").map(String.init))
        }

        for folder in folders {
            let candidate = folder + "/" + agent.command
            if manager.isExecutableFile(atPath: candidate) {
                return candidate
            }
        }

        return fromLoginShell(agent.command)
    }

    /// Asks the login shell where the command is. Only a real path is accepted, because a shell
    /// may answer with the name of a function that wraps it.
    private static func fromLoginShell(_ command: String) -> String? {
        let shell = ProcessInfo.processInfo.environment["SHELL"] ?? "/bin/zsh"

        guard let answer = try? run(
            path: shell,
            arguments: ["-lc", "command -v " + command],
            input: nil,
            workingFolder: NSTemporaryDirectory(),
            limit: 15),
            answer.code == 0 else {
            return nil
        }

        let found = answer.output.trimmingCharacters(in: .whitespacesAndNewlines)
        return found.hasPrefix("/") && FileManager.default.isExecutableFile(atPath: found) ? found : nil
    }

    /// Asks the app for its version, which proves it can actually run.
    static func version(_ agent: AiAgent, saved: String) throws -> String {
        guard let path = locate(agent, saved: saved) else {
            throw Failure.message(
                "\(agent.label) is not on this machine. Install it, then sign in with your \(agent.plan) account.")
        }

        let answer = try run(
            path: path,
            arguments: ["--version"],
            input: nil,
            workingFolder: NSTemporaryDirectory(),
            limit: 30)

        if answer.code != 0 {
            throw Failure.message("\(agent.label) could not be started. \(trim(answer))")
        }

        let first = answer.output.split(separator: "\n").first.map(String.init) ?? ""
        return first.isEmpty ? "\(agent.label) answered." : first
    }

    // ------------------------------------------------------------------ asking a question

    /// Puts one question to the signed in app and gives back what it said. The whole prompt goes
    /// in through the keyboard side of the pipe, so nothing has to survive being quoted, and
    /// everything is written inside a folder that is deleted afterwards.
    static func complete(
        settings: Settings,
        system: String,
        user: String,
        pictures: [Data]) throws -> String {
        guard let agent = find(settings.aiProvider) else {
            throw Failure.message(
                "That service has no app to sign in with. Choose a different service, or use a key.")
        }

        guard let path = locate(agent, saved: settings.aiCliPath) else {
            throw Failure.message(
                "\(agent.label) is not on this machine. Install it, then sign in with your \(agent.plan) account.")
        }

        let folder = NSTemporaryDirectory() + "stepwright-ai-" + String(UUID().uuidString.prefix(12))
        try FileManager.default.createDirectory(atPath: folder, withIntermediateDirectories: true)

        defer { try? FileManager.default.removeItem(atPath: folder) }

        var names: [String] = []
        for (index, picture) in pictures.enumerated() {
            let name = "screenshot\(index + 1).jpg"
            try picture.write(to: URL(fileURLWithPath: folder + "/" + name))
            names.append(name)
        }

        let answer = try run(
            path: path,
            arguments: arguments(agent: agent, model: settings.aiModel, folder: folder, pictures: names),
            input: prompt(agent: agent, system: system, user: user, pictures: names),
            workingFolder: folder,
            limit: 600)

        if answer.code != 0 {
            throw Failure.message("\(agent.label) stopped with an error. \(trim(answer))")
        }

        let text = read(agent: agent, folder: folder, output: answer.output)

        if text.isEmpty {
            throw Failure.message("\(agent.label) finished but said nothing. \(trim(answer))")
        }

        return text
    }

    private static func prompt(agent: AiAgent, system: String, user: String, pictures: [String]) -> String {
        var text = system + "\n\n"

        if !pictures.isEmpty && !agent.attachesPictures {
            // Nothing is attached for these apps, so the picture is named and they open it themselves.
            let names = agent.id == "gemini"
                ? pictures.map { "@" + $0 }.joined(separator: " ")
                : pictures.joined(separator: ", ")

            text += "Look at \(names) in this folder before you answer.\n\n"
        }

        return text + user
    }

    private static func arguments(agent: AiAgent, model: String, folder: String, pictures: [String]) -> [String] {
        var arguments: [String] = []
        let chosen = model.trimmingCharacters(in: .whitespacesAndNewlines)

        switch agent.id {
        case "anthropic":
            arguments += ["--print", "--output-format", "json"]

            if !chosen.isEmpty { arguments += ["--model", chosen] }

            // Reading the screenshot is the only thing it is allowed to do.
            if !pictures.isEmpty { arguments += ["--allowed-tools", "Read"] }

        case "openai":
            arguments += [
                "exec",
                "--skip-git-repo-check",
                "--sandbox", "read-only",
                "--color", "never",
                "--output-last-message", folder + "/" + answerFile,
            ]

            if !chosen.isEmpty { arguments += ["--model", chosen] }

            for picture in pictures { arguments += ["--image", folder + "/" + picture] }

            // A single dash tells it the question is arriving through the pipe.
            arguments.append("-")

        default:
            arguments += ["--output-format", "json"]
            if !chosen.isEmpty { arguments += ["--model", chosen] }
        }

        return arguments
    }

    private static let answerFile = "answer.txt"

    /// Each app wraps the answer differently. The file Codex writes is exact, the other two
    /// wrap it in a small envelope, and anything unexpected falls back to the raw output.
    private static func read(agent: AiAgent, folder: String, output: String) -> String {
        if agent.id == "openai" {
            let written = (try? String(contentsOfFile: folder + "/" + answerFile, encoding: .utf8)) ?? ""
            let trimmed = written.trimmingCharacters(in: .whitespacesAndNewlines)
            return trimmed.isEmpty ? output.trimmingCharacters(in: .whitespacesAndNewlines) : trimmed
        }

        if let data = output.data(using: .utf8),
           let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            for name in ["result", "response"] {
                if let text = json[name] as? String, !text.trimmingCharacters(in: .whitespaces).isEmpty {
                    return text.trimmingCharacters(in: .whitespacesAndNewlines)
                }
            }
        }

        return output.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    // ------------------------------------------------------------------ running the process

    private struct Answer {
        let code: Int32
        let output: String
        let error: String
    }

    private static func run(
        path: String,
        arguments: [String],
        input: String?,
        workingFolder: String,
        limit: TimeInterval) throws -> Answer {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = arguments
        process.currentDirectoryURL = URL(fileURLWithPath: workingFolder)

        // A key left in the environment would quietly move the work onto paid billing, which is
        // the one thing this route exists to avoid.
        var environment = ProcessInfo.processInfo.environment
        for name in [
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
            "OPENAI_API_KEY", "OPENAI_BASE_URL",
            "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI",
        ] {
            environment.removeValue(forKey: name)
        }

        process.environment = environment

        let out = Pipe()
        let bad = Pipe()
        let feed = Pipe()

        process.standardOutput = out
        process.standardError = bad
        process.standardInput = feed

        // Both pipes are drained as they fill, because a full pipe stops the app that is writing.
        var outData = Data()
        var badData = Data()
        let lock = NSLock()

        out.fileHandleForReading.readabilityHandler = { handle in
            let piece = handle.availableData
            lock.lock()
            outData.append(piece)
            lock.unlock()
        }

        bad.fileHandleForReading.readabilityHandler = { handle in
            let piece = handle.availableData
            lock.lock()
            badData.append(piece)
            lock.unlock()
        }

        do {
            try process.run()
        } catch {
            throw Failure.message("\((path as NSString).lastPathComponent) could not be started. \(error.localizedDescription)")
        }

        if let input {
            feed.fileHandleForWriting.write(Data(input.utf8))
        }

        try? feed.fileHandleForWriting.close()

        let deadline = Date().addingTimeInterval(limit)

        while process.isRunning && Date() < deadline {
            Thread.sleep(forTimeInterval: 0.05)
        }

        if process.isRunning {
            process.terminate()
            throw Failure.message(
                "\((path as NSString).lastPathComponent) was still running after \(Int(limit / 60)) minutes and was stopped.")
        }

        process.waitUntilExit()

        out.fileHandleForReading.readabilityHandler = nil
        bad.fileHandleForReading.readabilityHandler = nil

        // Whatever arrived after the last handler ran still belongs in the answer.
        lock.lock()
        outData.append(out.fileHandleForReading.availableData)
        badData.append(bad.fileHandleForReading.availableData)
        let output = String(data: outData, encoding: .utf8) ?? ""
        let error = String(data: badData, encoding: .utf8) ?? ""
        lock.unlock()

        return Answer(code: process.terminationStatus, output: output, error: error)
    }

    /// The most useful line to show a person when something went wrong.
    private static func trim(_ answer: Answer) -> String {
        let text = (answer.error.isEmpty ? answer.output : answer.error)
            .trimmingCharacters(in: .whitespacesAndNewlines)

        if text.isEmpty { return "It gave no reason." }

        return text.count <= 300 ? text : String(text.prefix(300)) + "..."
    }
}
