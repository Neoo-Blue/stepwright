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
    public static async Task<string> AskAsync(bool work, string question, CancellationToken token)
    {
        if (!Session.Remembered)
        {
            throw new InvalidOperationException(
                "Copilot is not signed in on this machine yet. Sign in in Settings, once.");
        }

        return await UiThread.RunAsync(async () =>
        {
            JsonNode? prepared = await DriveAsync(work, question, token).ConfigureAwait(true);

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

    /// <summary>
    /// Opens the page, finds the box, and puts the question in as true input: a real click to set
    /// the cursor, the text typed through the developer protocol, and a real Enter. Everything
    /// that has to be true rather than dispatched happens here.
    /// </summary>
    private static async Task<JsonNode?> DriveAsync(bool work, string question, CancellationToken token)
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

            await Session.ClickAsync(x, y, token).ConfigureAwait(true);
            await Task.Delay(200, token).ConfigureAwait(true);
            await Session.TypeAsync(Flatten(question), token).ConfigureAwait(true);
            await Task.Delay(500, token).ConfigureAwait(true);
            await Session.EnterAsync(token).ConfigureAwait(true);
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

          const answerFrom = () => {
            const fresh = blocks().filter(t => !before.has(t) && !isQuestion(t));
            if (!fresh.length) return '';
            fresh.sort((a, b) => a.length - b.length);
            return stripChrome(fresh[fresh.length - 1]);
          };

          await sleep(1200);

          // The question should have become a block once it was sent. If it did not, the Enter did
          // not take, so find the send button and press it.
          const sent = () => blocks().some(t => !before.has(t) && isQuestion(t));

          if (!sent()) {
            const button = deep().filter(b => {
              if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) return false;
              if (!visible(b) || b.disabled || b.getAttribute('aria-disabled') === 'true') return false;
              const l = ((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '') + ' ' + (b.getAttribute('data-testid') || '')).toLowerCase();
              return /send|submit/.test(l);
            }).pop();
            if (button) { button.click(); }
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

    /// <summary>Writes down every visible piece of text on the page, for the report.</summary>
    private static string DumpScript() =>
        "(async () => {" + Helpers + """
          const before = new Set(window.__swBeforeAll || []);
          const box = composer();
          return { composer: !!box, url: location.href, items: record(before) };
        })()
        """;
}
