import Foundation

/// Something a guide can be filed under, with the name a person recognises.
struct PublishTarget {
    let id: String
    let name: String
    var detail: String = ""

    var label: String { detail.isEmpty ? name : "\(name)   \(detail)" }
}

enum PublishDestination {
    case hudu
    case confluence
}

/// Talks to Hudu. The site keeps articles as HTML with the pictures carried inside them, so a
/// guide goes across in one request with nothing to attach afterwards.
struct HuduClient {
    private let base: String
    private let key: String

    init(site: String, key: String) throws {
        var address = site.trimmingCharacters(in: .whitespaces)
        while address.hasSuffix("/") { address.removeLast() }

        guard !address.isEmpty else {
            throw Failure.message("Hudu needs the address of your site.")
        }

        if !address.lowercased().hasPrefix("http") { address = "https://" + address }

        self.base = address
        self.key = key.trimmingCharacters(in: .whitespaces)
    }

    func check() async throws -> String {
        _ = try await send("GET", "/companies?page=1&page_size=1", nil)
        return "Connected to " + base + "."
    }

    /// Every company, plus the shared library that belongs to no company.
    func companies() async throws -> [PublishTarget] {
        var targets = [PublishTarget(id: "", name: "Global knowledge base", detail: "no company")]

        for company in try await pages("/companies", key: "companies") {
            if company["archived"] as? Bool == true { continue }
            guard let id = company["id"] as? Int, id > 0 else { continue }

            targets.append(PublishTarget(
                id: String(id),
                name: company["name"] as? String ?? "Company \(id)"))
        }

        return targets
    }

    func folders(company: String) async throws -> [PublishTarget] {
        var targets = [PublishTarget(id: "", name: "No folder", detail: "the top of the knowledge base")]

        let path = company.isEmpty ? "/folders" : "/folders?company_id=" + company

        for folder in try await pages(path, key: "folders") {
            guard let id = folder["id"] as? Int, id > 0 else { continue }

            // A folder belonging to a company must not appear under a different one.
            let owner = folder["company_id"].map { String(describing: $0) } ?? ""
            if !company.isEmpty, !owner.isEmpty, owner != company { continue }
            if company.isEmpty, !owner.isEmpty, owner != "0" { continue }

            targets.append(PublishTarget(
                id: String(id),
                name: folder["name"] as? String ?? "Folder \(id)"))
        }

        return targets
    }

    /// Articles already there, so an existing one can be replaced rather than doubled.
    func articles(company: String) async throws -> [PublishTarget] {
        var targets = [PublishTarget(id: "", name: "Create a new article")]

        let path = company.isEmpty ? "/articles" : "/articles?company_id=" + company

        for article in try await pages(path, key: "articles") {
            guard let id = article["id"] as? Int, id > 0 else { continue }

            targets.append(PublishTarget(
                id: String(id),
                name: article["name"] as? String ?? "Article \(id)",
                detail: "replace"))
        }

        return targets
    }

    /// Creates the article, or replaces one when an identifier is given.
    func publish(
        title: String,
        html: String,
        company: String,
        folder: String,
        article: String) async throws -> String {
        var payload: [String: Any] = ["name": title, "content": html]

        if let id = Int(company), id > 0 { payload["company_id"] = id }
        if let id = Int(folder), id > 0 { payload["folder_id"] = id }

        let existing = Int(article) ?? 0
        let reply = try await send(
            existing > 0 ? "PUT" : "POST",
            existing > 0 ? "/articles/\(existing)" : "/articles",
            ["article": payload])

        let created = (reply?["article"] as? [String: Any]) ?? reply ?? [:]

        if let url = created["url"] as? String, !url.isEmpty { return url }

        let id = created["id"] as? Int ?? existing
        return id > 0 ? "\(base)/a/\(id)" : base
    }

    // ------------------------------------------------------------------ plumbing

    private func pages(_ path: String, key: String) async throws -> [[String: Any]] {
        var all: [[String: Any]] = []
        let size = 100

        for page in 1...25 {
            let separator = path.contains("?") ? "&" : "?"
            let reply = try await send("GET", "\(path)\(separator)page=\(page)&page_size=\(size)", nil)

            let batch = (reply?[key] as? [[String: Any]]) ?? []
            if batch.isEmpty { break }

            all.append(contentsOf: batch)
            if batch.count < size { break }
        }

        return all
    }

    private func send(_ method: String, _ path: String, _ body: [String: Any]?) async throws -> [String: Any]? {
        guard let url = URL(string: base + "/api/v1" + path) else {
            throw Failure.message("That address cannot be used.")
        }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 120
        request.setValue(key, forHTTPHeaderField: "x-api-key")
        request.setValue("application/json", forHTTPHeaderField: "accept")

        if let body {
            request.setValue("application/json", forHTTPHeaderField: "content-type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }

        let (data, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw Failure.message("Hudu replied with \(http.statusCode). \(describe(data))")
        }

        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private func describe(_ data: Data) -> String {
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let message = json["error"] as? String { return message }
            if let message = json["message"] as? String { return message }
        }

        return String((String(data: data, encoding: .utf8) ?? "").prefix(300))
    }
}

/// Talks to Confluence. Unlike Hudu it will not take a picture carried inside the markup, so
/// a page goes across in two moves: the page is created, then each picture is attached to it
/// and referred to by name from the text that was already written.
struct ConfluenceClient {
    private let base: String
    private let auth: String

    init(site: String, email: String, token: String) throws {
        var address = site.trimmingCharacters(in: .whitespaces)
        while address.hasSuffix("/") { address.removeLast() }

        guard !address.isEmpty else {
            throw Failure.message("Confluence needs the address of your site.")
        }

        if !address.lowercased().hasPrefix("http") { address = "https://" + address }

        // The address is usually given as the site, while the api sits under wiki.
        self.base = address.lowercased().hasSuffix("/wiki") ? address : address + "/wiki"

        let pair = "\(email.trimmingCharacters(in: .whitespaces)):\(token.trimmingCharacters(in: .whitespaces))"
        self.auth = "Basic " + Data(pair.utf8).base64EncodedString()
    }

    func check() async throws -> String {
        _ = try await send("GET", "/api/v2/spaces?limit=1", nil)
        return "Connected to " + base + "."
    }

    func spaces() async throws -> [PublishTarget] {
        var targets: [PublishTarget] = []
        var cursor: String?

        for _ in 0..<20 {
            let path = "/api/v2/spaces?limit=100" + (cursor.map { "&cursor=" + $0 } ?? "")
            let reply = try await send("GET", path, nil)

            guard let results = reply?["results"] as? [[String: Any]] else { break }

            for space in results {
                let id = space["id"].map { String(describing: $0) } ?? ""
                if id.isEmpty { continue }

                targets.append(PublishTarget(
                    id: id,
                    name: space["name"] as? String ?? "Space " + id,
                    detail: space["key"] as? String ?? ""))
            }

            cursor = nextCursor(reply)
            if cursor == nil { break }
        }

        return targets.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    /// Pages already in a space, so a guide can be filed under one of them.
    func pages(space: String) async throws -> [PublishTarget] {
        var targets = [PublishTarget(id: "", name: "At the top of the space")]
        guard !space.isEmpty else { return targets }

        var cursor: String?

        for _ in 0..<10 {
            let path = "/api/v2/spaces/\(space)/pages?limit=100" + (cursor.map { "&cursor=" + $0 } ?? "")
            let reply = try await send("GET", path, nil)

            guard let results = reply?["results"] as? [[String: Any]] else { break }

            for item in results {
                let id = item["id"].map { String(describing: $0) } ?? ""
                if id.isEmpty { continue }

                targets.append(PublishTarget(
                    id: id,
                    name: item["title"] as? String ?? "Page " + id,
                    detail: "under this"))
            }

            cursor = nextCursor(reply)
            if cursor == nil { break }
        }

        return targets
    }

    /// Creates the page, then attaches each picture. The markup written earlier already
    /// refers to them by the names used here.
    func publish(
        title: String,
        storage: String,
        space: String,
        parent: String,
        pictures: [Int: Data],
        jpeg: Bool,
        progress: @escaping (String) -> Void) async throws -> String {
        var body: [String: Any] = [
            "spaceId": space,
            "status": "current",
            "title": title,
            "body": ["representation": "storage", "value": storage],
        ]

        if !parent.isEmpty { body["parentId"] = parent }

        progress("Creating the page...")
        let reply = try await send("POST", "/api/v2/pages", body)

        guard let pageId = reply?["id"].map({ String(describing: $0) }), !pageId.isEmpty else {
            throw Failure.message("Confluence created the page but did not say which one.")
        }

        var done = 0

        for number in pictures.keys.sorted() {
            done += 1
            progress("Attaching picture \(done) of \(pictures.count)...")

            try await attach(
                page: pageId,
                name: String(format: "step%03d.%@", number, jpeg ? "jpg" : "png"),
                data: pictures[number]!,
                jpeg: jpeg)
        }

        if let links = reply?["_links"] as? [String: Any], let web = links["webui"] as? String {
            return base + web
        }

        return "\(base)/pages/\(pageId)"
    }

    /// Attachments still go through the older interface, which is the only one that takes a
    /// file, and it insists on a header saying the request is deliberate.
    private func attach(page: String, name: String, data: Data, jpeg: Bool) async throws {
        guard let url = URL(string: "\(base)/rest/api/content/\(page)/child/attachment") else {
            throw Failure.message("That address cannot be used.")
        }

        let boundary = "stepwright" + UUID().uuidString
        var request = URLRequest(url: url)
        request.httpMethod = "PUT"
        request.timeoutInterval = 180
        request.setValue(auth, forHTTPHeaderField: "Authorization")
        request.setValue("no-check", forHTTPHeaderField: "X-Atlassian-Token")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")

        var payload = Data()
        payload.append("--\(boundary)\r\n".data(using: .utf8)!)
        payload.append("Content-Disposition: form-data; name=\"file\"; filename=\"\(name)\"\r\n".data(using: .utf8)!)
        payload.append("Content-Type: \(jpeg ? "image/jpeg" : "image/png")\r\n\r\n".data(using: .utf8)!)
        payload.append(data)
        payload.append("\r\n--\(boundary)\r\n".data(using: .utf8)!)
        payload.append("Content-Disposition: form-data; name=\"minorEdit\"\r\n\r\ntrue\r\n".data(using: .utf8)!)
        payload.append("--\(boundary)--\r\n".data(using: .utf8)!)
        request.httpBody = payload

        let (body, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw Failure.message(
                "The picture \(name) could not be attached. Confluence replied with \(http.statusCode). \(describe(body))")
        }
    }

    private func nextCursor(_ reply: [String: Any]?) -> String? {
        guard let links = reply?["_links"] as? [String: Any],
              let next = links["next"] as? String,
              let mark = next.range(of: "cursor=") else { return nil }

        let value = String(next[mark.upperBound...])
        return value.split(separator: "&").first.map(String.init)
    }

    private func send(_ method: String, _ path: String, _ body: [String: Any]?) async throws -> [String: Any]? {
        guard let url = URL(string: base + path) else {
            throw Failure.message("That address cannot be used.")
        }

        var request = URLRequest(url: url)
        request.httpMethod = method
        request.timeoutInterval = 180
        request.setValue(auth, forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")

        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        }

        let (data, response) = try await URLSession.shared.data(for: request)

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            throw Failure.message("Confluence replied with \(http.statusCode). \(describe(data))")
        }

        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private func describe(_ data: Data) -> String {
        if let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] {
            if let errors = json["errors"] as? [[String: Any]], let first = errors.first {
                let title = first["title"] as? String ?? ""
                let detail = first["detail"] as? String ?? ""
                let joined = [title, detail].filter { !$0.isEmpty }.joined(separator: " ")
                if !joined.isEmpty { return joined }
            }

            if let message = json["message"] as? String { return message }
        }

        return String((String(data: data, encoding: .utf8) ?? "").prefix(300))
    }
}
