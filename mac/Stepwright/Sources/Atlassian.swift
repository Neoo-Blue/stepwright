import AppKit
import Foundation
import Network

/// What a finished sign in leaves behind.
struct AtlassianSession {
    let accessToken: String
    let refreshToken: String
    let expires: Date

    /// Identifies the site the token may be used against.
    let cloudId: String

    /// The address a person recognises, for example https://yourcompany.atlassian.net.
    let siteUrl: String
    let siteName: String
}

/// Signing in to Atlassian the way their own documentation describes: the browser asks the
/// person, the answer comes back to a listener on this machine, and that answer is traded for
/// a token that can be renewed without asking again.
///
/// Atlassian issues these tokens to an application you register once, so the identifier and
/// the secret belong to your own company rather than to Stepwright.
enum Atlassian {
    /// The address the browser is sent back to. It has to match the one registered.
    static let callbackPort: UInt16 = 53682

    static var callbackUrl: String { "http://localhost:\(callbackPort)/callback" }

    static let consolePage = "https://developer.atlassian.com/console/myapps/"

    /// The older style of permission, which covers both interfaces this app uses. Attachments
    /// still go through the first one, and an application may not mix the two styles.
    private static let scopes = [
        "read:confluence-space.summary",
        "read:confluence-content.all",
        "write:confluence-content",
        "write:confluence-file",
        "offline_access",
    ]

    /// Opens the browser, waits for the person to agree, and comes back with a usable session.
    static func signIn(
        clientId: String,
        clientSecret: String,
        progress: @escaping (String) -> Void) async throws -> AtlassianSession {
        let id = clientId.trimmingCharacters(in: .whitespacesAndNewlines)
        let secret = clientSecret.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !id.isEmpty, !secret.isEmpty else {
            throw Failure.message(
                "Signing in needs the identifier and the secret of your own Atlassian application.")
        }

        let state = UUID().uuidString
        let waiter = CallbackWaiter(state: state)
        try waiter.start(port: callbackPort)

        defer { waiter.stop() }

        var address = "https://auth.atlassian.com/authorize?audience=api.atlassian.com"
        address += "&client_id=" + escape(id)
        address += "&scope=" + escape(scopes.joined(separator: " "))
        address += "&redirect_uri=" + escape(callbackUrl)
        address += "&state=" + state
        address += "&response_type=code&prompt=consent"

        progress("Waiting for the browser...")

        guard let url = URL(string: address) else {
            throw Failure.message("That sign in address cannot be used.")
        }

        NSWorkspace.shared.open(url)

        let code = try await waiter.code()

        progress("Trading the answer for a token...")

        let granted = try await post([
            "grant_type": "authorization_code",
            "client_id": id,
            "client_secret": secret,
            "code": code,
            "redirect_uri": callbackUrl,
        ])

        return try await finish(granted, previousRefresh: "")
    }

    /// Renews a session that has run out, without asking the person anything.
    static func refresh(
        clientId: String,
        clientSecret: String,
        refreshToken: String) async throws -> AtlassianSession {
        guard !refreshToken.isEmpty else {
            throw Failure.message("There is no sign in to renew. Sign in to Atlassian again.")
        }

        let granted = try await post([
            "grant_type": "refresh_token",
            "client_id": clientId.trimmingCharacters(in: .whitespacesAndNewlines),
            "client_secret": clientSecret.trimmingCharacters(in: .whitespacesAndNewlines),
            "refresh_token": refreshToken,
        ])

        // A renewal does not always hand back a new one, so the old one is kept.
        return try await finish(granted, previousRefresh: refreshToken)
    }

    private static func finish(
        _ granted: [String: Any],
        previousRefresh: String) async throws -> AtlassianSession {
        guard let access = granted["access_token"] as? String, !access.isEmpty else {
            throw Failure.message("Atlassian did not hand back a token.")
        }

        let seconds = granted["expires_in"] as? Int ?? 3600
        let refresh = granted["refresh_token"] as? String ?? previousRefresh
        let site = try await self.site(access: access)

        return AtlassianSession(
            accessToken: access,
            refreshToken: refresh,

            // A minute is taken off so a request cannot start on a token that ends mid flight.
            expires: Date().addingTimeInterval(TimeInterval(max(60, seconds - 60))),
            cloudId: site.cloudId,
            siteUrl: site.url,
            siteName: site.name)
    }

    /// Which site the token was granted for. The first is the only one for most people.
    private static func site(access: String) async throws -> (cloudId: String, url: String, name: String) {
        guard let url = URL(string: "https://api.atlassian.com/oauth/token/accessible-resources") else {
            throw Failure.message("That address cannot be used.")
        }

        var request = URLRequest(url: url)
        request.timeoutInterval = 60
        request.setValue("Bearer " + access, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw Failure.message(
                "Atlassian would not say which sites this sign in covers. It replied with \(http.statusCode).")
        }

        guard let sites = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]],
              let first = sites.first else {
            throw Failure.message(
                "This sign in covers no Confluence site. Check that the application has the Confluence permissions.")
        }

        var address = first["url"] as? String ?? ""
        while address.hasSuffix("/") { address.removeLast() }

        return (
            first["id"] as? String ?? "",
            address,
            first["name"] as? String ?? "your site")
    }

    private static func post(_ body: [String: Any]) async throws -> [String: Any] {
        guard let url = URL(string: "https://auth.atlassian.com/oauth/token") else {
            throw Failure.message("That address cannot be used.")
        }

        var request = URLRequest(url: url)
        request.httpMethod = "POST"
        request.timeoutInterval = 60
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: body)

        let (data, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            var detail = ""

            if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
                detail = json["error_description"] as? String ?? json["error"] as? String ?? ""
            }

            throw Failure.message(
                "Atlassian refused the sign in with \(http.statusCode). \(detail)"
                    .trimmingCharacters(in: .whitespaces))
        }

        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure.message("Atlassian sent nothing back.")
        }

        return json
    }

    private static func escape(_ value: String) -> String {
        value.addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? value
    }
}

/// Listens for the one request the browser makes when it comes back. Only the first line
/// matters, which is why this is a socket rather than anything larger.
private final class CallbackWaiter {
    private let state: String
    private let queue = DispatchQueue(label: "stepwright.atlassian.callback")
    private var listener: NWListener?

    /// Guards the single answer, since a browser may ask for more than one thing.
    private let lock = NSLock()
    private var answered = false
    private var waiting: CheckedContinuation<String, Error>?
    private var result: Result<String, Error>?

    init(state: String) {
        self.state = state
    }

    func start(port: UInt16) throws {
        let options = NWProtocolTCP.Options()
        options.noDelay = true

        let parameters = NWParameters(tls: nil, tcp: options)
        parameters.allowLocalEndpointReuse = true
        parameters.requiredInterfaceType = .loopback

        guard let endpoint = NWEndpoint.Port(rawValue: port),
              let listener = try? NWListener(using: parameters, on: endpoint) else {
            throw Failure.message(
                "Nothing could listen on port \(port). Close whatever is using it and try again.")
        }

        listener.newConnectionHandler = { [weak self] connection in
            self?.read(connection)
        }

        listener.start(queue: queue)
        self.listener = listener
    }

    func stop() {
        listener?.cancel()
        listener = nil
    }

    /// Waits up to five minutes for the browser to come back.
    func code() async throws -> String {
        queue.asyncAfter(deadline: .now() + 300) { [weak self] in
            self?.settle(.failure(Failure.message("Nothing came back from the browser.")))
        }

        return try await withCheckedThrowingContinuation { continuation in
            lock.lock()

            if let result {
                lock.unlock()
                continuation.resume(with: result)
                return
            }

            waiting = continuation
            lock.unlock()
        }
    }

    private func read(_ connection: NWConnection) {
        connection.start(queue: queue)

        connection.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, _, _ in
            guard let self else { return }

            let head = data.flatMap { String(data: $0, encoding: .utf8) } ?? ""
            let parts = head.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: false)
            let target = parts.count > 1 ? String(parts[1]) : ""

            // The browser asks for the icon as well, and that is not the answer being waited for.
            guard target.hasPrefix("/callback") else {
                self.reply(connection, "Nothing to see here.")
                return
            }

            let query = URLComponents(string: "http://localhost" + target)?.queryItems ?? []
            let value = { (name: String) in query.first { $0.name == name }?.value ?? "" }

            let error = value("error_description").isEmpty ? value("error") : value("error_description")

            if !error.isEmpty {
                self.reply(connection, "Atlassian said no. You can close this tab.")
                self.settle(.failure(Failure.message("Atlassian refused the sign in. " + error)))
                return
            }

            guard value("state") == self.state else {
                self.reply(connection, "That answer was not the one asked for.")
                self.settle(.failure(Failure.message("The answer from the browser did not match the request.")))
                return
            }

            let code = value("code")

            guard !code.isEmpty else {
                self.reply(connection, "That answer carried nothing.")
                return
            }

            self.reply(connection, "Stepwright is signed in. You can close this tab.")
            self.settle(.success(code))
        }
    }

    private func reply(_ connection: NWConnection, _ message: String) {
        let page = """
        <!doctype html><html><head><meta charset="utf-8"><title>Stepwright</title></head>\
        <body style="font-family:-apple-system,Helvetica,Arial,sans-serif;padding:48px;">\
        <h2>Stepwright</h2><p>\(message)</p></body></html>
        """

        let body = Data(page.utf8)
        var head = "HTTP/1.1 200 OK\r\n"
        head += "Content-Type: text/html; charset=utf-8\r\n"
        head += "Content-Length: \(body.count)\r\n"
        head += "Connection: close\r\n\r\n"

        connection.send(
            content: Data(head.utf8) + body,
            completion: .contentProcessed { _ in connection.cancel() })
    }

    /// The first answer wins, and anything after it is ignored.
    private func settle(_ outcome: Result<String, Error>) {
        lock.lock()

        if answered {
            lock.unlock()
            return
        }

        answered = true
        result = outcome
        let continuation = waiting
        waiting = nil
        lock.unlock()

        continuation?.resume(with: outcome)
    }
}
