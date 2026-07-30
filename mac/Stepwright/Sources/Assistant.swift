import AppKit
import Foundation

struct AiProvider {
    let id: String
    let label: String
    let baseUrl: String
    let model: String
    let keyPage: String
    let hint: String
}

enum AiProviders {
    static let all: [AiProvider] = [
        AiProvider(
            id: "openai",
            label: "OpenAI, the GPT models",
            baseUrl: "https://api.openai.com/v1",
            model: "gpt-4o-mini",
            keyPage: "https://platform.openai.com/api-keys",
            hint: "Key starts with sk. Any model that can read pictures works, for example gpt-4o."),
        AiProvider(
            id: "anthropic",
            label: "Anthropic, the Claude models",
            baseUrl: "https://api.anthropic.com",
            model: "claude-sonnet-5",
            keyPage: "https://console.anthropic.com/settings/keys",
            hint: "Key starts with sk ant. Every Claude model can read pictures."),
        AiProvider(
            id: "gemini",
            label: "Google, the Gemini models",
            baseUrl: "https://generativelanguage.googleapis.com",
            model: "gemini-2.0-flash",
            keyPage: "https://aistudio.google.com/apikey",
            hint: "Key from AI Studio. Every Gemini model can read pictures."),
        AiProvider(
            id: "custom",
            label: "Anything that speaks the OpenAI shape",
            baseUrl: "http://localhost:11434/v1",
            model: "llama3.2",
            keyPage: "",
            hint: "Your own gateway, a local model, or any service with a chat completions endpoint."),
    ]

    static func find(_ id: String) -> AiProvider {
        all.first { $0.id.caseInsensitiveCompare(id) == .orderedSame } ?? all[0]
    }
}

/// One request shape per service. The differences live here so nothing above has to care
/// which one is in use. Pictures are only ever sent when that switch is on.
enum AiClient {
    static func complete(
        settings: Settings,
        system: String,
        user: String,
        pictures: [Data]) async throws -> String {
        let provider = settings.aiProvider.lowercased()
        let auth = AiAuthKinds.clean(settings.aiAuth)

        // The subscription route never touches the request shapes below. The app that is
        // already signed in does the talking, and it is handed the same prompt.
        if auth == AiAuthKinds.cli {
            return try AiAgents.complete(settings: settings, system: system, user: user, pictures: pictures)
        }

        let subscriptionToken = auth == AiAuthKinds.token
        let key = subscriptionToken ? settings.aiToken : settings.aiKey
        let base = settings.aiBaseUrl.trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))

        guard !base.isEmpty else {
            throw Failure.message("The assistant has no address to call. Check Settings.")
        }

        if subscriptionToken && provider != "anthropic" {
            throw Failure.message(
                "Only Anthropic takes a subscription token. For the others use the app you signed in to, or a key.")
        }

        switch provider {
        case "anthropic":
            return try await anthropic(
                base: base,
                key: key,
                model: settings.aiModel,
                system: system,
                user: user,
                pictures: pictures,
                subscriptionToken: subscriptionToken)
        case "gemini":
            return try await gemini(base: base, key: key, model: settings.aiModel, system: system, user: user, pictures: pictures)
        default:
            return try await openAi(base: base, key: key, model: settings.aiModel, system: system, user: user, pictures: pictures)
        }
    }

    /// True for the Claude models that removed the temperature setting rather than ignoring
    /// it: the 5 family, and 4.6 onwards. The plain names sonnet, opus and haiku are the
    /// current models, so they belong here too. Anything older is left alone.
    private static func dropsTemperature(_ model: String) -> Bool {
        let name = model.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()

        if name.isEmpty || name == "sonnet" || name == "opus" || name == "haiku" { return true }
        guard name.hasPrefix("claude") else { return false }

        return ["-5", "-4-6", "-4-7", "-4-8", "fable", "mythos"].contains { name.contains($0) }
    }

    /// Puts the right kind of proof on an Anthropic request.
    private static func sign(_ request: inout URLRequest, key: String, subscriptionToken: Bool) {
        if !key.isEmpty {
            if subscriptionToken {
                request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization")
                request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
            } else {
                request.setValue(key, forHTTPHeaderField: "x-api-key")
            }
        }

        request.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
    }

    /// Asks the service which models the key can actually use.
    static func listModels(settings: Settings) async throws -> [String] {
        let provider = settings.aiProvider.lowercased()
        let auth = AiAuthKinds.clean(settings.aiAuth)

        // There is nothing to ask when the app does the talking, so the names it knows are offered.
        if auth == AiAuthKinds.cli {
            return AiAgents.find(provider)?.models ?? []
        }

        let subscriptionToken = auth == AiAuthKinds.token
        let key = subscriptionToken ? settings.aiToken : settings.aiKey
        let base = settings.aiBaseUrl.trimmingCharacters(in: .whitespaces)
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))

        guard !base.isEmpty else { throw Failure.message("Fill in the address first.") }

        var address: String
        var headers: [String: String] = [:]

        switch provider {
        case "anthropic":
            address = base.hasSuffix("/v1") ? base + "/models?limit=100" : base + "/v1/models?limit=100"

            if subscriptionToken {
                headers["Authorization"] = "Bearer " + key
                headers["anthropic-beta"] = "oauth-2025-04-20"
            } else {
                headers["x-api-key"] = key
            }

            headers["anthropic-version"] = "2023-06-01"
        case "gemini":
            let root = base.contains("/v1") ? base : base + "/v1beta"
            address = root + "/models?pageSize=200"
            headers["x-goog-api-key"] = key
        default:
            address = base + "/models"
            headers["Authorization"] = "Bearer " + key
        }

        guard let url = URL(string: address) else { throw Failure.message("That address cannot be used.") }

        var request = URLRequest(url: url)
        for (name, value) in headers where !value.isEmpty {
            request.setValue(value, forHTTPHeaderField: name)
        }

        let json = try await send(request)
        var names: [String] = []

        if provider == "gemini" {
            for model in (json["models"] as? [[String: Any]]) ?? [] {
                guard let name = model["name"] as? String, name.hasPrefix("models/") else { continue }

                let methods = model["supportedGenerationMethods"] as? [String]
                if methods == nil || methods!.contains("generateContent") {
                    names.append(String(name.dropFirst("models/".count)))
                }
            }
        } else {
            for model in (json["data"] as? [[String: Any]]) ?? [] {
                if let id = model["id"] as? String { names.append(id) }
            }
        }

        // Anything that cannot hold a conversation is noise in a list you pick from.
        let unrelated = ["whisper", "tts", "dall-e", "embedding", "moderation", "audio", "realtime", "image"]

        return Array(Set(names.filter { name in
            !unrelated.contains { name.lowercased().contains($0) }
        })).sorted()
    }

    // ------------------------------------------------------------------ shapes

    private static func openAi(
        base: String,
        key: String,
        model: String,
        system: String,
        user: String,
        pictures: [Data]) async throws -> String {
        var content: [[String: Any]] = [["type": "text", "text": user]]

        for picture in pictures {
            content.append([
                "type": "image_url",
                "image_url": ["url": "data:image/jpeg;base64," + picture.base64EncodedString()],
            ])
        }

        let body: [String: Any] = [
            "model": model,
            "temperature": 0.2,
            "messages": [
                ["role": "system", "content": system],
                ["role": "user", "content": content],
            ],
        ]

        var request = try post(base + "/chat/completions", body)
        if !key.isEmpty { request.setValue("Bearer " + key, forHTTPHeaderField: "Authorization") }

        let json = try await send(request)
        let choices = json["choices"] as? [[String: Any]]
        let message = choices?.first?["message"] as? [String: Any]
        return message?["content"] as? String ?? ""
    }

    private static func anthropic(
        base: String,
        key: String,
        model: String,
        system: String,
        user: String,
        pictures: [Data],
        subscriptionToken: Bool) async throws -> String {
        var content: [[String: Any]] = []

        // The picture reads better before the question about it.
        for picture in pictures {
            content.append([
                "type": "image",
                "source": [
                    "type": "base64",
                    "media_type": "image/jpeg",
                    "data": picture.base64EncodedString(),
                ],
            ])
        }

        content.append(["type": "text", "text": user])

        // A subscription token is granted to the Claude Code client, and the service only
        // honours it when the request looks like one, so the first system block is that
        // client's own line and the house style follows it. A bought key needs none of this.
        let instructions: Any = subscriptionToken
            ? [
                ["type": "text", "text": "You are Claude Code, Anthropic's official CLI for Claude."],
                ["type": "text", "text": system],
              ]
            : system

        // Claude thinks before it answers unless told otherwise, and this ceiling covers the
        // thinking as well as the sentence, so it is set well above what a step needs.
        var body: [String: Any] = [
            "model": model,
            "max_tokens": 8192,
            "system": instructions,
            "messages": [["role": "user", "content": content]],
        ]

        // Every current Claude model has dropped the temperature setting and rejects a request
        // that carries one. The older ones still take it.
        if !dropsTemperature(model) { body["temperature"] = 0.2 }

        let address = base.hasSuffix("/v1") ? base + "/messages" : base + "/v1/messages"
        var request = try post(address, body)
        sign(&request, key: key, subscriptionToken: subscriptionToken)

        let json = try await send(request)
        let blocks = json["content"] as? [[String: Any]] ?? []

        return blocks.compactMap { block in
            (block["type"] as? String) == "text" ? block["text"] as? String : nil
        }.joined()
    }

    private static func gemini(
        base: String,
        key: String,
        model: String,
        system: String,
        user: String,
        pictures: [Data]) async throws -> String {
        var parts: [[String: Any]] = [["text": user]]

        for picture in pictures {
            parts.append([
                "inlineData": [
                    "mimeType": "image/jpeg",
                    "data": picture.base64EncodedString(),
                ],
            ])
        }

        let body: [String: Any] = [
            "systemInstruction": ["parts": [["text": system]]],
            "contents": [["role": "user", "parts": parts]],
            "generationConfig": ["temperature": 0.2],
        ]

        let root = base.contains("/v1") ? base : base + "/v1beta"
        var request = try post("\(root)/models/\(model):generateContent", body)
        if !key.isEmpty { request.setValue(key, forHTTPHeaderField: "x-goog-api-key") }

        let json = try await send(request)
        let candidates = json["candidates"] as? [[String: Any]]
        let content = candidates?.first?["content"] as? [String: Any]
        let parts2 = content?["parts"] as? [[String: Any]] ?? []

        return parts2.compactMap { $0["text"] as? String }.joined()
    }

    private static func post(_ address: String, _ body: [String: Any]) throws -> URLRequest {
        guard let url = URL(string: address) else {
            throw Failure.message("That address cannot be used.")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 180
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        return request
    }

    private static func send(_ request: URLRequest) async throws -> [String: Any] {
        let (data, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw Failure.message("The assistant replied with \(http.statusCode). \(describe(data))")
        }

        guard let json = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure.message("The assistant sent something that could not be read.")
        }

        return json
    }

    /// Pulls the readable part out of an error body where there is one.
    private static func describe(_ data: Data) -> String {
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let error = json["error"] as? [String: Any],
               let message = error["message"] as? String {
                return message
            }

            if let message = json["message"] as? String { return message }
        }

        let text = String(data: data, encoding: .utf8) ?? ""
        return String(text.prefix(300))
    }
}

/// The optional writing pass: rewrites each step and, where it helps, adds a note.
enum Assistant {
    private static let houseStyle = """
    You write instructions for a step by step guide that someone will follow at a computer. \
    Write one short sentence in the imperative, starting with a verb. \
    Name the exact button, link, menu or field, in quotes, using the words that appear on screen. \
    Keep any value the person typed. Do not invent a step, do not merge steps, do not change the order. \
    Never use a hyphen, an en dash or an em dash anywhere in your answer.
    """

    private static let noteStyle = """
    A note is optional. Add one only when it genuinely helps the reader: a warning, \
    something to have ready first, or what they should expect to happen. \
    Leave the note empty when the step speaks for itself. Never restate the step.
    """

    static func improve(
        guide: Guide,
        settings: Settings,
        pictureFor: @escaping (Step) -> Data?,
        progress: @escaping (String) -> Void) async throws -> Int {
        let targets = guide.Steps.filter { $0.Kind != .heading && !$0.Text.isEmpty }
        guard !targets.isEmpty else { return 0 }

        var changed = 0

        if settings.aiSendScreenshots {
            for (index, step) in targets.enumerated() {
                progress("Looking at step \(index + 1) of \(targets.count)")

                let picture = pictureFor(step)
                let system = houseStyle
                    + " You are shown the screenshot taken at the moment of the action."
                    + " A red circle marks where the click landed, and a red outline marks the control that was used."
                    + " Trust what you can see in the picture over the description you are given. "
                    + (settings.aiWriteNotes ? noteStyle + " " : "Leave the note empty. ")
                    + "Reply with JSON only in the form {\"text\":\"...\",\"note\":\"...\"}."

                var context = "Guide title: \(guide.Title)\n"
                context += "This is step \(index + 1) of \(targets.count).\n"
                context += describe(step)

                let reply = try await AiClient.complete(
                    settings: settings,
                    system: system,
                    user: context,
                    pictures: picture.map { [$0] } ?? [])

                if apply(step: step, json: parse(reply), settings: settings) { changed += 1 }
            }

            return changed
        }

        let system = houseStyle + " "
            + (settings.aiWriteNotes ? noteStyle + " " : "Leave every note empty. ")
            + "Reply with JSON only in the form {\"steps\":[{\"i\":1,\"text\":\"...\",\"note\":\"...\"}]}."

        let batchSize = 15
        var offset = 0

        while offset < targets.count {
            let batch = Array(targets[offset..<min(offset + batchSize, targets.count)])
            progress("Rewriting steps \(offset + 1) to \(offset + batch.count) of \(targets.count)")

            var payload = "Guide title: \(guide.Title)\nRewrite each of these steps.\n"

            for (index, step) in batch.enumerated() {
                payload += "\nStep \(index + 1)\n" + describe(step)
            }

            let reply = try await AiClient.complete(
                settings: settings,
                system: system,
                user: payload,
                pictures: [])

            if let json = parse(reply), let list = json["steps"] as? [[String: Any]] {
                for entry in list {
                    guard let position = entry["i"] as? Int, position >= 1, position <= batch.count else { continue }
                    if apply(step: batch[position - 1], json: entry, settings: settings) { changed += 1 }
                }
            }

            offset += batchSize
        }

        return changed
    }

    static func suggestHeading(guide: Guide, settings: Settings) async throws -> (String, String) {
        var payload = "These are the steps of a guide:\n"

        for (index, step) in guide.visible.prefix(40).enumerated() {
            payload += "\(index + 1). \(step.Text)\n"
        }

        let rules = """
        Name the procedure these steps describe. \
        Reply with JSON only in the form {"title":"...","summary":"..."}. \
        The title is at most eight words and starts with a verb ending in ing, \
        for example Resetting a password. The summary is one sentence. \
        Never use a hyphen, an en dash or an em dash.
        """

        let reply = try await AiClient.complete(settings: settings, system: rules, user: payload, pictures: [])
        guard let json = parse(reply) else { return ("", "") }

        return (tidy(json["title"] as? String ?? ""), tidy(json["summary"] as? String ?? ""))
    }

    static func test(settings: Settings) async throws -> String {
        let reply = try await AiClient.complete(
            settings: settings,
            system: "Reply with the single word ready.",
            user: "ping",
            pictures: [])

        return reply.isEmpty ? "The endpoint answered but sent no text." : reply.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func describe(_ step: Step) -> String {
        var text = "  what the recorder wrote: \(step.Text)\n"
        if !step.AppName.isEmpty { text += "  application: \(step.AppName)\n" }
        if !step.WindowTitle.isEmpty { text += "  window: \(step.WindowTitle)\n" }
        if !step.ElementName.isEmpty { text += "  control the system reported: \(step.ElementName) (\(step.ElementType))\n" }
        if !step.TypedText.isEmpty { text += "  text typed: \(step.TypedText)\n" }
        if !step.Keys.isEmpty { text += "  keys pressed: \(step.Keys)\n" }
        if step.Redacted { text += "  this step is a secret value and must stay hidden\n" }
        return text
    }

    private static func apply(step: Step, json: [String: Any]?, settings: Settings) -> Bool {
        guard let json else { return false }
        var changed = false

        if let text = json["text"] as? String, !text.isEmpty {
            let clean = tidy(text)
            if clean != step.Text {
                step.Text = clean
                changed = true
            }
        }

        if settings.aiWriteNotes, let note = json["note"] as? String, !note.isEmpty {
            let clean = tidy(note)
            if clean != step.Notes {
                step.Notes = clean
                changed = true
            }
        }

        return changed
    }

    private static func parse(_ reply: String) -> [String: Any]? {
        guard let start = reply.firstIndex(of: "{"), let end = reply.lastIndex(of: "}"), start < end else { return nil }
        let slice = String(reply[start...end])
        guard let data = slice.data(using: .utf8) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func tidy(_ text: String) -> String {
        var result = text.trimmingCharacters(in: .whitespacesAndNewlines)

        // The house style forbids dashes, so any that slip through are rewritten.
        result = result.replacingOccurrences(of: " \u{2014} ", with: ", ")
            .replacingOccurrences(of: " \u{2013} ", with: ", ")
            .replacingOccurrences(of: "\u{2014}", with: " ")
            .replacingOccurrences(of: "\u{2013}", with: " ")

        while result.contains("  ") {
            result = result.replacingOccurrences(of: "  ", with: " ")
        }

        return result
    }
}
