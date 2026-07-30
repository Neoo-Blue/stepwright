import Foundation

/// What the shaping pass did, so a person can be told and can put it back.
struct ShapeResult {
    var folded = 0
    var split = 0
    var hidden = 0

    var changed: Bool { folded > 0 || split > 0 || hidden > 0 }

    func describe() -> String {
        var parts: [String] = []

        if folded > 0 { parts.append("folded \(folded) steps into the ones before them") }
        if split > 0 { parts.append("split \(split) steps that held two actions") }
        if hidden > 0 { parts.append("set \(hidden) steps aside") }

        return parts.joined(separator: ", ")
    }
}

/// The second half of the assistant. The rewriting pass fixes the words of a step; this one
/// fixes how many steps there are.
///
/// A recorder writes one step per action, which is finer than any reader wants. Opening a tab,
/// typing an address and pressing Enter are three recorded actions and one instruction. This
/// pass decides which runs of steps are really one, which single step is really two, and which
/// carry nothing at all. Nothing is deleted: a step that is folded away or left out is set
/// aside, so it stays in the editor and can be brought back.
enum Shaper {
    /// How many steps are considered at once. Folding only happens inside a batch.
    private static let batchSize = 30

    private static let rules = """
    You are editing a step by step guide so that someone can follow it quickly. \
    A recorder wrote one step for every action, which is finer than a reader wants. \
    Your job is the shape of the guide, not only the words.
    Fold a run of steps into one when a reader would do them as a single action. \
    Opening a tab, typing an address and pressing Enter is one step: go to that address. \
    Clicking a menu and then the item inside it is one step. Typing into a field and \
    pressing Enter is one step. Filling in the fields of one form is one step, whether they \
    were typed or chosen from a list: give every field and every value in the order they were \
    filled, and fold the button that submits the form in with them.
    A step whose only content is a key that moved between fields, such as Tab, belongs with \
    the typing on either side of it rather than standing on its own.
    Split a step into two when it holds two actions the reader has to do separately.
    Leave a step out when it carries nothing a reader has to do. That includes a stray \
    click, a repeat of the step before it, and opening something the next step opens \
    anyway. It also includes anything a later step undoes or replaces: a value that was \
    typed and then typed again differently, a search that was abandoned, a page that was \
    opened and then left without doing anything there. The person was working, not \
    performing, and their false starts are not instructions.
    The title and the summary say what the guide is for. A step that does not carry the \
    reader towards that is noise, however carefully it was recorded.
    Keep the order. Only fold steps that sit next to each other.
    Every value the person typed and every keyboard shortcut must survive somewhere, \
    unless the step it came from is left out as noise. Aim for the shortest guide that \
    still tells the reader everything they have to do.
    Write one short sentence per step, in the imperative, starting with a verb, naming \
    the exact button, link, menu or field in quotes. \
    A note is optional and is for the reader: a warning, something to have ready first, or \
    what they should expect to happen. Leave it empty when the step speaks for itself. Never \
    restate the step, and never write a note about the guide or about how it was recorded, \
    such as saying that a value was hidden. \
    Never use a hyphen, an en dash or an em dash anywhere in your answer.
    Reply with JSON only, in this form:
    {"steps":[{"from":[1,2,3],"picture":3,"text":"...","note":"..."}],"leaveOut":[7]}
    from lists the numbers of the original steps this one is made of, in order. \
    picture says which of them to show, normally the last, or the one where the action \
    actually landed. To split a step, give two entries with the same single from. \
    leaveOut lists steps that carry nothing. Every number must appear once, either in a \
    from list or in leaveOut.
    """

    /// Reshapes the guide in place. Headings are left where they are, because they are the
    /// person's own structure rather than something the recorder guessed at.
    static func shape(
        guide: Guide,
        settings: Settings,
        progress: @escaping (String) -> Void) async throws -> ShapeResult {
        var result = ShapeResult()

        let targets = guide.Steps.filter { $0.Kind != .heading && !$0.Skip && !$0.Text.isEmpty }
        guard targets.count > 1 else { return result }

        var offset = 0

        while offset < targets.count {
            let batch = Array(targets[offset..<min(offset + batchSize, targets.count)])
            progress("Deciding the shape of steps \(offset + 1) to \(offset + batch.count)...")

            var payload = "Guide title: \(guide.Title)\n"
            payload += "These are steps \(offset + 1) to \(offset + batch.count) of \(targets.count).\n"
            payload += "Number them from 1 as they appear below.\n"

            for (index, step) in batch.enumerated() {
                payload += "\nStep \(index + 1)\n" + describe(step)
            }

            let reply = try await AiClient.complete(
                settings: settings,
                system: rules,
                user: payload,
                pictures: [])

            apply(guide: guide, batch: batch, reply: reply, result: &result)
            offset += batchSize
        }

        return result
    }

    private static func describe(_ step: Step) -> String {
        var text = "  \(step.Text)\n"
        if !step.AppName.isEmpty { text += "  application: \(step.AppName)\n" }
        if !step.WindowTitle.isEmpty { text += "  window: \(step.WindowTitle)\n" }
        if !step.TypedText.isEmpty { text += "  text typed: \(step.TypedText)\n" }
        if !step.Keys.isEmpty { text += "  keys pressed: \(step.Keys)\n" }
        if !step.Notes.isEmpty { text += "  note underneath: \(step.Notes)\n" }
        if step.Redacted { text += "  this step is a secret value and must stay hidden\n" }
        return text
    }

    /// Carries out the plan against the real guide. Everything here is deliberately cautious:
    /// a plan that does not add up is ignored rather than half applied, and no step is ever
    /// removed from the list, only set aside.
    private static func apply(guide: Guide, batch: [Step], reply: String, result: inout ShapeResult) {
        guard let plan = parse(reply), let entries = plan["steps"] as? [[String: Any]] else { return }

        // Read first, because a fold is allowed to reach over steps that are being left out.
        var leaving = Set<Int>()
        for value in (plan["leaveOut"] as? [Int]) ?? [] where value >= 1 && value <= batch.count {
            leaving.insert(value)
        }

        var spoken = Set<Int>()
        var work: [(primary: Step, folded: [Step], text: String, note: String, clone: Bool)] = []

        for entry in entries {
            guard let from = entry["from"] as? [Int], !from.isEmpty else { continue }

            let numbers = from.filter { $0 >= 1 && $0 <= batch.count }
            guard numbers.count == from.count else { continue }

            // A fold has to read forwards, and may only reach over steps that are being left
            // out anyway. Anything else would quietly reorder the guide, and a guide in the
            // wrong order is worse than a long one.
            let forwards = zip(numbers, numbers.dropFirst()).allSatisfy { $1 > $0 }
            let overNothing = numbers.count < 2
                || (numbers[0]...numbers[numbers.count - 1]).allSatisfy {
                    numbers.contains($0) || leaving.contains($0)
                }

            guard forwards, overNothing else { continue }

            var chosen = entry["picture"] as? Int ?? numbers[numbers.count - 1]
            if !numbers.contains(chosen) { chosen = numbers[numbers.count - 1] }

            let split = numbers.count == 1 && spoken.contains(numbers[0])

            work.append((
                primary: batch[chosen - 1],
                folded: numbers.filter { $0 != chosen }.map { batch[$0 - 1] },
                text: tidy(entry["text"] as? String ?? ""),
                note: tidy(entry["note"] as? String ?? ""),
                clone: split))

            for number in numbers { spoken.insert(number) }
        }

        for item in work {
            if item.clone {
                // A step that became two: the second half is a copy, so both halves keep the
                // same picture and marker.
                let copy = item.primary.copy()
                copy.Text = item.text
                copy.Notes = item.note

                if let at = guide.Steps.firstIndex(where: { $0 === item.primary }) {
                    guide.Steps.insert(copy, at: at + 1)
                } else {
                    guide.Steps.append(copy)
                }

                result.split += 1
                continue
            }

            if !item.text.isEmpty { item.primary.Text = item.text }
            item.primary.Notes = item.note
            item.primary.Skip = false

            for gone in item.folded {
                gone.Skip = true
                result.folded += 1
            }
        }

        for value in leaving where !spoken.contains(value) {
            batch[value - 1].Skip = true
            result.hidden += 1
        }
    }

    private static func parse(_ reply: String) -> [String: Any]? {
        guard let start = reply.firstIndex(of: "{"),
              let end = reply.lastIndex(of: "}"),
              start < end,
              let data = String(reply[start...end]).data(using: .utf8) else { return nil }

        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func tidy(_ text: String) -> String {
        var result = text.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: " \u{2014} ", with: ", ")
            .replacingOccurrences(of: " \u{2013} ", with: ", ")
            .replacingOccurrences(of: "\u{2014}", with: " ")
            .replacingOccurrences(of: "\u{2013}", with: " ")

        while result.contains("  ") {
            result = result.replacingOccurrences(of: "  ", with: " ")
        }

        return result
    }
}
