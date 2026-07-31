import Foundation

/// What a finished Microsoft sign in leaves behind.
struct MicrosoftSession {
    let accessToken: String
    let refreshToken: String
    let expires: Date
}

/// Signing in to Microsoft the way a desktop application is meant to: the app asks for a code,
/// the person types that code into their browser on any machine, and the app waits. There is no
/// redirect address to register and no secret to keep, which suits an application that is handed
/// out as a file rather than hosted anywhere.
enum Microsoft {
    static let portalPage = "https://entra.microsoft.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"

    /// Everything the Copilot chat interface asks for. Microsoft requires all of them together,
    /// and offline access on top so the sign in can renew itself.
    static let copilotScopes = [
        "https://graph.microsoft.com/Sites.Read.All",
        "https://graph.microsoft.com/Mail.Read",
        "https://graph.microsoft.com/People.Read.All",
        "https://graph.microsoft.com/OnlineMeetingTranscript.Read.All",
        "https://graph.microsoft.com/Chat.Read",
        "https://graph.microsoft.com/ChannelMessage.Read.All",
        "https://graph.microsoft.com/ExternalItem.Read.All",
        "offline_access",
    ]

    /// What Azure AI Foundry asks for. One permission, plus offline access.
    static let foundryScopes = [
        "https://cognitiveservices.azure.com/.default",
        "offline_access",
    ]

    /// Work accounts only, because Copilot is not offered to personal ones.
    static func authority(_ tenant: String?) -> String {
        let named = (tenant ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return "https://login.microsoftonline.com/" + (named.isEmpty ? "organizations" : named) + "/oauth2/v2.0/"
    }

    /// Asks Microsoft for a code, shows it to the person, and waits until they have typed it in.
    /// Nothing is polled faster than Microsoft asks for, because answering too eagerly is what
    /// gets a client throttled.
    static func signIn(
        clientId: String,
        tenant: String?,
        scopes: [String],
        show: @escaping (String, String) -> Void) async throws -> MicrosoftSession {
        let id = clientId.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !id.isEmpty else {
            throw Failure.message("Signing in needs the identifier of your own Microsoft application.")
        }

        let start = try await form(
            authority(tenant) + "devicecode",
            ["client_id": id, "scope": scopes.joined(separator: " ")])

        guard let code = start["user_code"] as? String,
              let deviceCode = start["device_code"] as? String,
              !code.isEmpty, !deviceCode.isEmpty else {
            throw Failure.message("Microsoft did not hand back a code to sign in with.")
        }

        let where_ = start["verification_uri"] as? String ?? "https://microsoft.com/devicelogin"
        show(code, where_)

        var wait = max(2, start["interval"] as? Int ?? 5)
        let until = Date().addingTimeInterval(TimeInterval(max(60, start["expires_in"] as? Int ?? 900)))

        while Date() < until {
            try await Task.sleep(nanoseconds: UInt64(wait) * 1_000_000_000)

            let (granted, error) = await tryForm(
                authority(tenant) + "token",
                [
                    "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                    "client_id": id,
                    "device_code": deviceCode,
                ])

            if let granted { return try finish(granted, previousRefresh: "") }

            switch error {
            case "authorization_pending":
                continue
            case "slow_down":
                wait += 5
            case "authorization_declined":
                throw Failure.message("The sign in was declined.")
            case "expired_token":
                throw Failure.message("The code ran out before it was used. Try again.")
            default:
                throw Failure.message("Microsoft refused the sign in. " + error)
            }
        }

        throw Failure.message("The code ran out before it was used. Try again.")
    }

    /// Renews a sign in that has run out, without asking the person anything.
    static func refresh(
        clientId: String,
        tenant: String?,
        scopes: [String],
        refreshToken: String) async throws -> MicrosoftSession {
        guard !refreshToken.isEmpty else {
            throw Failure.message("There is no sign in to renew. Sign in to Microsoft again.")
        }

        let granted = try await form(
            authority(tenant) + "token",
            [
                "grant_type": "refresh_token",
                "client_id": clientId.trimmingCharacters(in: .whitespacesAndNewlines),
                "refresh_token": refreshToken,
                "scope": scopes.joined(separator: " "),
            ])

        return try finish(granted, previousRefresh: refreshToken)
    }

    private static func finish(_ granted: [String: Any], previousRefresh: String) throws -> MicrosoftSession {
        guard let access = granted["access_token"] as? String, !access.isEmpty else {
            throw Failure.message("Microsoft did not hand back a token.")
        }

        let seconds = granted["expires_in"] as? Int ?? 3600

        return MicrosoftSession(
            accessToken: access,
            refreshToken: granted["refresh_token"] as? String ?? previousRefresh,

            // A minute is taken off so a request cannot start on a token that ends mid flight.
            expires: Date().addingTimeInterval(TimeInterval(max(60, seconds - 60))))
    }

    private static func form(_ url: String, _ fields: [String: String]) async throws -> [String: Any] {
        let (body, error) = await tryForm(url, fields)

        guard let body else {
            throw Failure.message("Microsoft refused the request. " + error)
        }

        return body
    }

    /// Posts a form and hands back either the answer or the error name. The waiting half of the
    /// device flow is built entirely out of expected errors, so they are not thrown here.
    private static func tryForm(_ url: String, _ fields: [String: String]) async -> ([String: Any]?, String) {
        guard let address = URL(string: url) else { return (nil, "that address cannot be used") }

        var request = URLRequest(url: address)
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("application/x-www-form-urlencoded", forHTTPHeaderField: "Content-Type")

        let encoded = fields.map { name, value in
            let safe = value.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? value
            return name + "=" + safe
        }.joined(separator: "&")

        request.httpBody = Data(encoded.utf8)

        guard let (data, response) = try? await URLSession.shared.data(for: request) else {
            return (nil, "Microsoft could not be reached")
        }

        let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any]
        let ok = (response as? HTTPURLResponse).map { (200...299).contains($0.statusCode) } ?? false

        if ok, let json { return (json, "") }

        var name = (json?["error"] as? String) ?? ""
        let detail = (json?["error_description"] as? String) ?? ""

        if name.isEmpty {
            name = String((String(data: data, encoding: .utf8) ?? "").prefix(220))
        } else if !detail.isEmpty && name != "authorization_pending" && name != "slow_down" {
            name += ". " + String(detail.prefix(220))
        }

        return (nil, name)
    }
}
