using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stepwright.Web;

/// <summary>
/// Microsoft Copilot, reached the way the person reaches it: through the page they already have.
///
/// The other route to Copilot goes through Microsoft Graph, which means an application registered
/// in a tenant and an administrator approving four permissions that only an administrator can
/// approve. That is the correct route for a company that will do it once and be done. It is also
/// a wall in front of the one technician who just wants to write a guide this afternoon.
///
/// This route has no wall. Copilot is opened in a window, the person signs in with the account
/// they already use, and afterwards Stepwright puts its question into the same page and reads the
/// answer back out. Nothing is registered, nothing is approved, no token is held, and the licence
/// being used is the one the person is already paying for.
///
/// The question is typed as true input rather than as a dispatched event, because the editor
/// Microsoft builds ignores an event a script fakes: the placeholder never clears and the send
/// never arms. True input, made through the browser's own developer protocol, is accepted the
/// same as a person's, which is what finally made this send at all.
///
/// What it still costs: this reads a web page, and web pages get redesigned. The reading looks for
/// what a chat page always has rather than for the names this month's version uses, and when
/// Microsoft changes it enough to matter the failure is a plain sentence, and a page report can be
/// saved to see exactly what changed.
/// </summary>
public static class CopilotWeb
{
    /// <summary>Work and school Copilot. The consumer one lives elsewhere and answers differently.</summary>
    public const string WorkPage = "https://m365.cloud.microsoft/chat";

    public const string PersonalPage = "https://copilot.microsoft.com";

    private static WebSession? _session;

    public static WebSession Session => _session ??= new WebSession("copilot");

    /// <summary>True when somebody has signed in on this machine before.</summary>
    public static bool Remembered => Session.Remembered;

    /// <summary>
    /// Signs in, in a window the person drives themselves. It closes on its own once the chat
    /// page is actually open, so nobody has to guess when it worked.
    /// </summary>
    public static Task<bool> SignInAsync(IWin32Window? owner, bool work) => UiThread.RunAsync(async () =>
        await Session
            .ShowAsync(
                owner,
                work ? WorkPage : PersonalPage,
                "Sign in to Microsoft Copilot",
                finished: at => Landed(at, work))
            .ConfigureAwait(true));

    public static void SignOut()
    {
        _session?.Forget();
        _session = null;
    }

    // ------------------------------------------------------------------ asking it something

    /// <summary>
    /// Puts one question to Copilot and gives back what it said. Each question starts fresh, so
    /// nothing from the last step colours the next one.
    /// </summary>
    public static async Task<string> AskAsync(
        bool work,
        string question,
        CancellationToken token,
        IReadOnlyList<byte[]>? pictures = null)
    {
        if (!Session.Remembered)
        {
            throw new InvalidOperationException(
                "Copilot is not signed in on this machine yet. Sign in in Settings, once.");
        }

        List<string> files = Written(pictures);

        try
        {
            return await UiThread.RunAsync(async () =>
            {
                JsonNode? prepared = await DriveAsync(work, question, token, files).ConfigureAwait(true);

                if (prepared?["ok"]?.GetValue<bool>() != true)
                {
                    throw new InvalidOperationException(
                        "Stepwright could not find the box to type the question into. "
                        + (prepared?["error"]?.GetValue<string>()
                           ?? "The page may still be loading, or it keeps the chat inside a part this cannot reach."));
                }

                JsonNode? answered = await Session.RunAsync(HarvestScript(question), token).ConfigureAwait(true);
                return Read(answered);
            }).ConfigureAwait(false);
        }
        finally
        {
            foreach (string file in files)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // A picture the browser still holds open is cleared on the next run instead.
                }
            }
        }
    }

    /// <summary>
    /// Puts the pictures somewhere the browser can reach them. A page takes a file from disk
    /// rather than bytes in memory, so there has to be a file, and it is deleted the moment the
    /// question has gone.
    /// </summary>
    private static List<string> Written(IReadOnlyList<byte[]>? pictures)
    {
        var files = new List<string>();

        foreach (byte[] picture in pictures ?? Array.Empty<byte[]>())
        {
            try
            {
                string path = Path.Combine(
                    Path.GetTempPath(),
                    "stepwright-" + Guid.NewGuid().ToString("n")[..12] + ".png");

                File.WriteAllBytes(path, picture);
                files.Add(path);
            }
            catch (IOException)
            {
                // A picture that cannot be written is simply not sent; the words still are.
            }
        }

        return files;
    }

    /// <summary>
    /// Opens the page, finds the box, and puts the question in as true input: a real click to set
    /// the cursor, the text typed through the developer protocol, and a real Enter. Everything
    /// that has to be true rather than dispatched happens here.
    /// </summary>
    private static async Task<JsonNode?> DriveAsync(
        bool work,
        string question,
        CancellationToken token,
        IReadOnlyList<string>? files = null)
    {
        await Session.GoAsync(work ? WorkPage : PersonalPage, token).ConfigureAwait(true);

        if (!Landed(Session.Address, work))
        {
            throw new InvalidOperationException(
                "Copilot is asking to be signed in to again. Open Settings and sign in to Copilot.");
        }

        JsonNode? prepared = await Session.RunAsync(PrepareScript(), token).ConfigureAwait(true);

        if (prepared?["ok"]?.GetValue<bool>() == true)
        {
            double x = prepared["x"]?.GetValue<double>() ?? 0;
            double y = prepared["y"]?.GetValue<double>() ?? 0;

            string asked = Flatten(question);

            // The picture goes on before the words, because an attachment takes a moment to be
            // taken up and that moment is better spent while the question is being typed. A
            // picture that will not attach is not a failure: the words still go, and the step is
            // written from them, which is what happened before pictures were possible at all.
            if (files is { Count: > 0 })
            {
                bool attached = await Session.AttachAsync(FileBox, files, token).ConfigureAwait(true);

                if (attached)
                {
                    await ReadyForSendAsync(token).ConfigureAwait(true);
                }
            }

            await Session.ClickAsync(x, y, token).ConfigureAwait(true);
            await Task.Delay(200, token).ConfigureAwait(true);
            await Session.TypeAsync(asked, token).ConfigureAwait(true);

            // A long step carries a long question, and a long question takes the editor longer to
            // take in. Waiting a flat moment was enough for a short one and not for a real one,
            // which is why this worked and then did not. The wait grows with the question.
            int settle = Math.Min(6000, 700 + (asked.Length / 2));
            await Task.Delay(settle, token).ConfigureAwait(true);

            // What is on the page is noted once the question is in the box, because the greeting
            // under it is rewritten every so often and a note taken earlier would call the new
            // greeting an answer.
            await Session.RunAsync(NoteScript(), token).ConfigureAwait(true);

            // Sending is tried rather than assumed. Each round asks the page what actually
            // happened: whether the words are in the box at all, and whether the question has
            // posted. Nothing here is taken on trust, because every failure so far has been a
            // step that was assumed to have worked.
            for (int round = 0; round < 3; round++)
            {
                JsonNode? state = await Session.RunAsync(StateScript(question), token).ConfigureAwait(true);

                if (state?["posted"]?.GetValue<bool>() == true)
                {
                    break;
                }

                // The words never landed, so they are typed again rather than sending an empty box.
                if (state?["inBox"]?.GetValue<bool>() != true)
                {
                    await Session.ClickAsync(x, y, token).ConfigureAwait(true);
                    await Task.Delay(200, token).ConfigureAwait(true);
                    await Session.TypeAsync(asked, token).ConfigureAwait(true);
                    await Task.Delay(settle, token).ConfigureAwait(true);
                }

                await Session.EnterAsync(token).ConfigureAwait(true);
                await Task.Delay(1500, token).ConfigureAwait(true);

                JsonNode? after = await Session.RunAsync(StateScript(question), token).ConfigureAwait(true);

                if (after?["posted"]?.GetValue<bool>() == true)
                {
                    break;
                }

                // The Enter was not what sends here. The send button is pressed truly, with a real
                // click at its own place, because this page ignores a click a script calls for.
                if (after?["x"] is not null)
                {
                    await Session
                        .ClickAsync(after["x"]!.GetValue<double>(), after["y"]!.GetValue<double>(), token)
                        .ConfigureAwait(true);

                    await Task.Delay(1500, token).ConfigureAwait(true);
                }
            }
        }

        return prepared;
    }

    /// <summary>Turns what the harvest returned into either an answer or a reason it failed.</summary>
    private static string Read(JsonNode? answered)
    {
        if (answered is null)
        {
            throw new InvalidOperationException(
                "The Copilot page did not answer at all. Try again, or use the work account route or a key.");
        }

        string stage = answered["stage"]?.GetValue<string>() ?? string.Empty;
        string? failed = answered["error"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(failed))
        {
            string where = stage switch
            {
                "send" => "Stepwright typed the question but could not send it",
                "answer" => "Stepwright sent the question but could not read the answer",
                _ => "The Copilot page could not be used",
            };

            string sample = (answered["sample"]?.GetValue<string>() ?? string.Empty).Replace('\n', ' ').Trim();
            if (sample.Length > 160)
            {
                sample = sample[..160] + "...";
            }

            string tail = sample.Length > 0 ? " The new text on the page was: " + sample : string.Empty;

            throw new InvalidOperationException($"{where}. {failed}.{tail}");
        }

        string text = answered["text"]?.GetValue<string>() ?? string.Empty;

        return text.Trim().Length == 0
            ? throw new InvalidOperationException(
                "Copilot answered but Stepwright could not read the answer off the page. Try again, or use a key.")
            : text.Trim();
    }

    // ------------------------------------------------------------------ seeing the page

    /// <summary>
    /// Sends the question and then writes down everything the page holds, so the reading of the
    /// answer can be built against what is really there. This is the debugging eye.
    /// </summary>
    public static async Task<string> DiagnoseAsync(bool work, string question, CancellationToken token)
    {
        if (!Session.Remembered)
        {
            throw new InvalidOperationException("Copilot is not signed in on this machine yet. Sign in in Settings, once.");
        }

        JsonNode? dumped = await UiThread.RunAsync(async () =>
        {
            JsonNode? prepared = await DriveAsync(work, question, token).ConfigureAwait(true);

            // A little longer than a normal harvest, because a report is worth waiting for a full
            // answer to appear.
            await Task.Delay(6000, token).ConfigureAwait(true);

            JsonNode? report = await Session.RunAsync(DumpScript(), token).ConfigureAwait(true);

            return report ?? prepared;
        }).ConfigureAwait(false);

        return Format(dumped, question);
    }

    /// <summary>Turns the page report into something a person can read and paste.</summary>
    private static string Format(JsonNode? dumped, string question)
    {
        if (dumped is null)
        {
            return "The page returned nothing at all.";
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine("Stepwright Copilot page report");
        report.AppendLine("Version " + Stepwright.Build.Version);
        report.AppendLine("Question sent: " + question);

        if (dumped["error"]?.GetValue<string>() is string failed)
        {
            report.AppendLine("Note: " + failed);
        }

        report.AppendLine("Composer found: " + (dumped["composer"]?.GetValue<bool>() ?? false));
        report.AppendLine("Address: " + (dumped["url"]?.GetValue<string>() ?? string.Empty));
        report.AppendLine();
        report.AppendLine("Every visible piece of text, newest marked with a star. The columns are:");
        report.AppendLine("[new] tag role testid | topPx heightPx len controls block | text");
        report.AppendLine(new string('-', 70));

        if (dumped["items"] is JsonArray items)
        {
            foreach (JsonNode? item in items)
            {
                if (item is null)
                {
                    continue;
                }

                string star = (item["fresh"]?.GetValue<bool>() ?? false) ? "*" : " ";
                string tag = item["tag"]?.GetValue<string>() ?? "?";
                string role = item["role"]?.GetValue<string>() ?? "-";
                string testid = item["testid"]?.GetValue<string>() ?? "-";
                int top = item["top"]?.GetValue<int>() ?? 0;
                int height = item["h"]?.GetValue<int>() ?? 0;
                int len = item["len"]?.GetValue<int>() ?? 0;
                int controls = item["controls"]?.GetValue<int>() ?? 0;
                string block = (item["block"]?.GetValue<bool>() ?? false) ? "Y" : "n";
                string text = item["text"]?.GetValue<string>() ?? string.Empty;

                report.AppendLine($"{star} {tag} {role} {testid} | {top} {height} {len} c{controls} {block} | {text}");
            }
        }

        if (dumped["buttons"] is JsonArray buttons && buttons.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Every button on the page. The columns are:");
            report.AppendLine("[greyed] testid | leftPx topPx | label");
            report.AppendLine(new string('-', 70));

            foreach (JsonNode? button in buttons)
            {
                if (button is null)
                {
                    continue;
                }

                string off = (button["off"]?.GetValue<bool>() ?? false) ? "x" : " ";
                string testid = button["testid"]?.GetValue<string>() ?? "-";
                int left = button["left"]?.GetValue<int>() ?? 0;
                int top = button["top"]?.GetValue<int>() ?? 0;
                string label = button["label"]?.GetValue<string>() ?? string.Empty;

                report.AppendLine($"{off} {testid} | {left} {top} | {label}");
            }
        }

        return report.ToString();
    }

    // ------------------------------------------------------------------ where the page is

    /// <summary>
    /// True once the browser is somewhere that is the chat itself rather than a step on the way to
    /// it. Sign in bounces through several Microsoft addresses, and every one would otherwise look
    /// like arrival.
    /// </summary>
    private static bool Landed(string address, bool work)
    {
        if (address.Length == 0
            || address.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
            || address.Contains("login.live.com", StringComparison.OrdinalIgnoreCase)
            || address.Contains("/oauth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return work
            ? address.Contains("m365.cloud.microsoft", StringComparison.OrdinalIgnoreCase)
              || address.Contains("microsoft365.com", StringComparison.OrdinalIgnoreCase)
            : address.Contains("copilot.microsoft.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string Flatten(string question) =>
        Regex.Replace(question ?? string.Empty, @"\s+", " ").Trim();

    // ------------------------------------------------------------------ the scripts

    /// <summary>
    /// The shared eyes of every script: how to reach every element through shadow roots and
    /// frames, what counts as visible, what a message block is, and how to describe an element.
    /// The blocks are the heart of it: a block is a visible piece of prose that holds its own
    /// words and no controls, which is what an answer is and what a sidebar, a toolbar and a row
    /// of suggested prompts are not.
    /// </summary>
    private const string Helpers = """
          const sleep = ms => new Promise(r => setTimeout(r, ms));
          const norm = s => (s || '').replace(/\s+/g, ' ').trim();

          const deep = () => {
            const out = [];
            const stack = [document];
            const seen = new Set();
            while (stack.length) {
              const root = stack.pop();
              if (!root || seen.has(root)) continue;
              seen.add(root);
              let all = [];
              try { all = root.querySelectorAll ? [...root.querySelectorAll('*')] : []; } catch (e) { all = []; }
              for (const el of all) {
                out.push(el);
                if (el.shadowRoot) stack.push(el.shadowRoot);
                if (el.tagName === 'IFRAME') { try { if (el.contentDocument) stack.push(el.contentDocument); } catch (e) {} }
              }
            }
            return out;
          };

          const visible = el => {
            if (!el) return false;
            let b; try { b = el.getBoundingClientRect(); } catch (e) { return false; }
            if (b.width < 40 || b.height < 8) return false;
            const st = (el.ownerDocument.defaultView || window).getComputedStyle(el);
            return st && st.visibility !== 'hidden' && st.display !== 'none';
          };

          const leaf = (el, t) => {
            for (const c of el.children) {
              let ct = ''; try { ct = norm(c.innerText); } catch (e) {}
              if (ct.length >= t.length * 0.9) return false;
            }
            return true;
          };

          const controlsIn = el => {
            try { return el.querySelectorAll('a,button,[role="button"],[role="link"],[role="tab"],[role="menuitem"],[role="option"]').length; } catch (e) { return 0; }
          };

          const isBlock = (el, t) => {
            const tag = el.tagName;
            if (tag === 'BUTTON' || tag === 'A' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'NAV' || tag === 'LI') return false;
            if (el.isContentEditable) return false;
            try { if (el.closest && el.closest('nav,[role="navigation"],[role="list"],[role="listbox"],header,footer')) return false; } catch (e) {}
            if (controlsIn(el) >= 3) return false;
            return true;
          };

          const blocks = () => {
            const out = [];
            for (const el of deep()) {
              if (!visible(el)) continue;
              let t; try { t = norm(el.innerText); } catch (e) { continue; }
              if (t.length < 2 || t.length > 8000) continue;
              if (!leaf(el, t)) continue;
              if (!isBlock(el, t)) continue;
              out.push(t);
            }
            return out;
          };

          const allLeaves = () => {
            const out = [];
            for (const el of deep()) {
              if (!visible(el)) continue;
              let t; try { t = norm(el.innerText); } catch (e) { continue; }
              if (t.length < 2 || t.length > 4000) continue;
              if (!leaf(el, t)) continue;
              out.push(t);
            }
            return out;
          };

          const composer = () => {
            const boxes = deep().filter(el => {
              if (!visible(el)) return false;
              if (el.isContentEditable) return true;
              const tag = el.tagName;
              if (tag === 'TEXTAREA') return true;
              if (tag === 'INPUT' && (el.type === 'text' || el.type === 'search')) return true;
              return el.getAttribute && el.getAttribute('role') === 'textbox';
            });
            boxes.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);
            return boxes[boxes.length - 1] || null;
          };

          const record = before => {
            const items = [];
            for (const el of deep()) {
              if (!visible(el)) continue;
              let t; try { t = norm(el.innerText); } catch (e) { continue; }
              if (t.length < 2 || t.length > 4000) continue;
              if (!leaf(el, t)) continue;
              let box; try { box = el.getBoundingClientRect(); } catch (e) { box = { top: 0, height: 0 }; }
              items.push({
                tag: el.tagName,
                role: (el.getAttribute && el.getAttribute('role')) || '-',
                testid: (el.getAttribute && (el.getAttribute('data-testid') || el.getAttribute('data-test-id'))) || '-',
                top: box.top | 0,
                h: box.height | 0,
                len: t.length,
                controls: controlsIn(el),
                block: isBlock(el, t),
                fresh: !before.has(t),
                text: t.slice(0, 160),
              });
            }
            return items;
          };
        """;

    /// <summary>
    /// Waits for the box, notes what is on the page before anything is sent, puts the cursor in
    /// the box, and hands back the point to click so the real input lands in the right place.
    /// </summary>
    private static string PrepareScript() =>
        "(async () => {" + Helpers + """
          let box = null;
          const appear = Date.now() + 30000;
          while (Date.now() < appear) { box = composer(); if (box) break; await sleep(500); }

          if (!box) {
            window.__swBefore = [];
            window.__swBeforeAll = [];
            return { ok: false, error: 'The page may still be loading, or it keeps the chat inside a part this cannot reach.' };
          }

          window.__swBefore = blocks();
          window.__swBeforeAll = allLeaves();

          try { box.scrollIntoView({ block: 'center' }); } catch (e) {}
          box.focus();
          try {
            const r = document.createRange();
            r.selectNodeContents(box);
            r.collapse(false);
            const s = (box.ownerDocument.defaultView || window).getSelection();
            s.removeAllRanges();
            s.addRange(r);
          } catch (e) {}

          const b = box.getBoundingClientRect();
          return { ok: true, x: b.left + Math.min(60, b.width / 2), y: b.top + b.height / 2 };
        })()
        """;

    /// <summary>
    /// Finds the box a page keeps for files. It is nearly always there and nearly always hidden,
    /// which is exactly what is wanted: the file can be handed over without the file dialog ever
    /// opening. One that takes pictures is preferred over one that takes anything.
    /// </summary>
    private const string FileBox = """
        (() => {
          const found = [];
          const stack = [document];
          const seen = new Set();
          while (stack.length) {
            const root = stack.pop();
            if (!root || seen.has(root)) continue;
            seen.add(root);
            let all = [];
            try { all = root.querySelectorAll ? [...root.querySelectorAll('*')] : []; } catch (e) { all = []; }
            for (const el of all) {
              if (el.tagName === 'INPUT' && el.type === 'file') found.push(el);
              if (el.shadowRoot) stack.push(el.shadowRoot);
              if (el.tagName === 'IFRAME') { try { if (el.contentDocument) stack.push(el.contentDocument); } catch (e) {} }
            }
          }
          if (!found.length) return null;
          const pictures = found.filter(el => (el.accept || '').toLowerCase().includes('image'));
          return pictures[0] || found[0];
        })()
        """;

    /// <summary>
    /// Waits for an attached picture to finish being taken up. A page will not send while it is
    /// still carrying a picture up, so sending before it is ready loses the picture, the question
    /// or both.
    /// </summary>
    private static async Task ReadyForSendAsync(CancellationToken token)
    {
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(1000, token).ConfigureAwait(true);

            JsonNode? state = await Session.RunAsync(AttachedScript, token).ConfigureAwait(true);

            if (state?["ready"]?.GetValue<bool>() == true)
            {
                return;
            }
        }
    }

    /// <summary>
    /// True once a picture is on the message and nothing is still being carried up. A page shows
    /// what it is carrying and shows a progress bar while it carries, so the one without the
    /// other is the moment to send.
    /// </summary>
    private const string AttachedScript = """
        (() => {
          const busy = document.querySelectorAll('[role="progressbar"], progress');
          for (const b of busy) {
            let box; try { box = b.getBoundingClientRect(); } catch (e) { continue; }
            if (box.width > 4 && box.height > 2) return { ready: false, why: 'still carrying it up' };
          }
          return { ready: true };
        })()
        """;

    /// <summary>
    /// Notes what is on the page at the moment the question is in the box and about to go. Taken
    /// here rather than earlier because the greeting under the box is rewritten every so often,
    /// and a note taken before it settled would leave the newest greeting looking like an answer.
    /// </summary>
    private static string NoteScript() =>
        "(async () => {" + Helpers + """
          window.__swBefore = blocks();
          window.__swBeforeAll = allLeaves();
          return { noted: window.__swBefore.length };
        })()
        """;

    /// <summary>
    /// Says what actually happened: whether the words are in the box, whether the question has
    /// posted as a turn of its own, and where the send button is. Everything the sending does is
    /// decided by this rather than by assuming the last act worked.
    /// </summary>
    private static string StateScript(string question)
    {
        string asked = JsonSerializer.Serialize(question);

        return "(async () => {" + Helpers + """
          const q1 = norm(__QUESTION__);
          const before = new Set(window.__swBefore || []);
          const head = q1.slice(0, Math.min(30, q1.length));

          // The question has posted when the page shows it as a turn, which it marks, or when an
          // answer is already there to be read.
          const asked2 = deep().filter(el => {
            const id = el.getAttribute && (el.getAttribute('data-testid') || el.getAttribute('data-test-id'));
            return id === 'chatOutput' || id === 'copilot-message-div';
          });

          let posted = asked2.some(el => {
            let t = ''; try { t = norm(el.innerText); } catch (e) {}
            return t.length > 0 && (t === q1 || (q1.length > 10 && t.indexOf(head) === 0)
              || (el.getAttribute('data-testid') === 'copilot-message-div' && t.length > 12));
          });

          if (!posted) {
            posted = blocks().some(t => !before.has(t) && (t === q1 || (q1.length > 10 && t.indexOf(head) === 0)));
          }

          // Whether the words are actually sitting in the box, so an empty box is never sent.
          const box = composer();
          let inBox = false;
          if (box) {
            let t = ''; try { t = norm(box.innerText || box.value || ''); } catch (e) {}
            inBox = t.length > 8 && (t.indexOf(head) >= 0 || q1.indexOf(t.slice(0, 30)) >= 0);
          }

          if (posted) { return { posted: true, inBox: inBox }; }

          const buttons = deep().filter(b => {
            if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) return false;
            let box; try { box = b.getBoundingClientRect(); } catch (e) { return false; }
            if (box.width < 8 || box.height < 8) return false;
            const st = (b.ownerDocument.defaultView || window).getComputedStyle(b);
            if (!st || st.visibility === 'hidden' || st.display === 'none') return false;
            if (b.disabled || b.getAttribute('aria-disabled') === 'true') return false;
            const l = ((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' '
              + (b.getAttribute('data-testid') || '') + ' ' + (b.textContent || '')).toLowerCase();
            return /send|submit/.test(l);
          });

          if (!buttons.length) { return { posted: false, inBox: inBox }; }

          // The one lowest on the page is the one by the box, rather than one in a menu above it.
          buttons.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);
          const at = buttons[buttons.length - 1].getBoundingClientRect();

          return { posted: false, inBox: inBox, x: at.left + at.width / 2, y: at.top + at.height / 2 };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the answer. It is the largest block that is new since the question was sent and is
    /// not the question read back. If the question never became a block the trusted Enter did not
    /// send it, so a send button is pressed. When no answer can be found, the new blocks are
    /// handed back so a report shows what appeared.
    /// </summary>
    private static string HarvestScript(string question)
    {
        string asked = JsonSerializer.Serialize(question);

        return "(async () => {" + Helpers + """
          const q1 = norm(__QUESTION__);
          const before = new Set(window.__swBefore || []);

          const stripChrome = s => {
            const chrome = [
              /copilot can make mistakes[\s\S]*$/i,
              /ai-generated content may be incorrect[\s\S]*$/i,
              /message copilot[\s\S]*$/i,
              /ask me anything[\s\S]*$/i,
            ];
            for (const p of chrome) s = s.replace(p, '');
            return s.trim();
          };

          const isQuestion = t => t === q1 || (q1.length > 10 && t.indexOf(q1.slice(0, Math.min(30, q1.length))) === 0);

          // The page greets the person with a rotating suggestion, so a greeting is new every time
          // it changes and would otherwise read as an answer. It never is one.
          const greeting = t => /^(hi|hello|welcome|good (morning|afternoon|evening))\b/i.test(t)
            || /^try asking/i.test(t)
            || /how can i help/i.test(t);

          // What the page calls its own parts. Copilot marks the question and the answer plainly,
          // so they are read by those marks first: it is exact where guessing by size was not,
          // and it is what a redesign would have to change on purpose rather than by accident.
          const marked = name => deep().filter(el => {
            const id = el.getAttribute && (el.getAttribute('data-testid') || el.getAttribute('data-test-id'));
            return id === name;
          });

          const answerByMark = () => {
            const said = marked('copilot-message-div');
            if (!said.length) return '';

            const last = said[said.length - 1];
            let t = '';
            try { t = norm(last.innerText); } catch (e) { return ''; }

            // The suggestions and the buttons under an answer sit inside it, and they always come
            // after what was said. So the text is cut at the first of them rather than having
            // their words struck out wherever they appear, which would quietly damage an answer
            // that happened to contain the same phrase.
            let aside = [];
            try {
              aside = [...last.querySelectorAll('[role="toolbar"], button, [role="button"]')]
                .map(e => { try { return norm(e.innerText); } catch (x) { return ''; } })
                .filter(s => s.length > 2);
            } catch (e) {}

            t = t.replace(/^copilot said:\s*/i, '');

            let cut = t.length;
            for (const piece of aside) {
              const at = t.indexOf(piece);
              if (at > 0 && at < cut) { cut = at; }
            }

            const notice = t.search(/ai-generated content may be incorrect/i);
            if (notice > 0 && notice < cut) { cut = notice; }

            t = t.slice(0, cut);

            return stripChrome(norm(t));
          };

          const answerFrom = () => {
            const exact = answerByMark();
            if (exact) return exact;

            // Nothing marked, so fall back to reading the page as blocks.
            const fresh = blocks().filter(t => !before.has(t) && !isQuestion(t) && !greeting(t));
            if (!fresh.length) return '';
            fresh.sort((a, b) => a.length - b.length);
            return stripChrome(fresh[fresh.length - 1]);
          };

          // The question becomes a turn of its own once it has actually gone. Until that happens
          // nothing on the page can be an answer to it, and saying otherwise is how a greeting
          // came back as a reply. So this waits for the question to post, and if it never does it
          // says the question was never sent rather than inventing something.
          // The question has gone once the page shows it as a turn of its own, which it marks, or
          // failing that once it appears as a block outside the box. An answer already showing is
          // proof enough on its own.
          const posted = () => {
            const asked = marked('chatOutput');
            for (const el of asked) {
              let t = ''; try { t = norm(el.innerText); } catch (e) {}
              if (isQuestion(t)) return true;
            }

            if (answerByMark()) return true;

            return blocks().some(t => !before.has(t) && isQuestion(t));
          };

          let went = false;
          const goes = Date.now() + 25000;
          while (Date.now() < goes) {
            if (posted()) { went = true; break; }
            await sleep(700);
          }

          if (!went) {
            const fresh = blocks().filter(t => !before.has(t));
            return {
              stage: 'send',
              error: 'the question stayed in the box, so Copilot never received it',
              sample: fresh.slice(-6).join(' | ').slice(0, 300),
            };
          }

          let text = '';
          let steady = '';
          let still = 0;
          const until = Date.now() + 150000;

          while (Date.now() < until) {
            await sleep(1000);
            const now = answerFrom();
            if (now && now === steady) {
              still += 1;
              if (still >= 3) { text = now; break; }
            } else {
              still = 0;
            }
            steady = now;
          }

          if (!text) { text = answerFrom(); }

          if (!text) {
            const fresh = blocks().filter(t => !before.has(t));
            return { stage: 'answer', error: 'nothing that reads as an answer appeared', sample: fresh.slice(-8).join(' | ').slice(0, 300) };
          }

          return { text: text };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// Writes down every visible piece of text on the page, and every button with the name it
    /// answers to and whether it is greyed. The buttons matter as much as the text: when a
    /// question will not send, the send button and its state are the thing worth seeing.
    /// </summary>
    private static string DumpScript() =>
        "(async () => {" + Helpers + """
          const before = new Set(window.__swBeforeAll || []);
          const box = composer();

          const buttons = [];
          for (const b of deep()) {
            if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) continue;
            let box2; try { box2 = b.getBoundingClientRect(); } catch (e) { continue; }
            if (box2.width < 4 || box2.height < 4) continue;
            const st = (b.ownerDocument.defaultView || window).getComputedStyle(b);
            if (!st || st.visibility === 'hidden' || st.display === 'none') continue;
            buttons.push({
              label: norm((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '')).slice(0, 60),
              testid: (b.getAttribute('data-testid') || b.getAttribute('data-test-id') || '-'),
              off: !!(b.disabled || b.getAttribute('aria-disabled') === 'true'),
              top: box2.top | 0,
              left: box2.left | 0,
            });
          }

          return { composer: !!box, url: location.href, items: record(before), buttons: buttons };
        })()
        """;
}
