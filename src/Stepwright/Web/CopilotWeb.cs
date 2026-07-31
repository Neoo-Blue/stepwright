using System.Text.Json;
using System.Text.Json.Nodes;

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
/// What it costs: this reads a web page, and web pages get redesigned. Everything below is
/// written to survive that as far as it can, by looking for what a chat page always has rather
/// than for the names this month's version happens to use. When Microsoft does change it enough
/// to matter, the failure is a plain sentence saying so, not a wrong answer.
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

    // ------------------------------------------------------------------ seeing the page

    /// <summary>
    /// Sends the question and then writes down everything the page holds, so the reading of the
    /// answer can be built against what is really there rather than against a guess. This is the
    /// debugging eye: it does not try to be clever, it just reports every piece of text on the
    /// page, what element it sits in, and whether it is new since the question was sent.
    /// </summary>
    public static async Task<string> DiagnoseAsync(bool work, string question, CancellationToken token)
    {
        if (!Session.Remembered)
        {
            throw new InvalidOperationException("Copilot is not signed in on this machine yet. Sign in in Settings, once.");
        }

        JsonNode? dumped = await UiThread.RunAsync(async () =>
        {
            await Session.GoAsync(work ? WorkPage : PersonalPage, token).ConfigureAwait(true);

            if (!Landed(Session.Address, work))
            {
                throw new InvalidOperationException("Copilot is asking to be signed in to again. Sign in in Settings.");
            }

            return await Session.RunAsync(DebugScript(question), token).ConfigureAwait(true);
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

        if (dumped["error"]?.GetValue<string>() is string failed)
        {
            return "Could not read the page. " + failed;
        }

        var report = new System.Text.StringBuilder();
        report.AppendLine("Stepwright Copilot page report");
        report.AppendLine("Version " + Stepwright.Build.Version);
        report.AppendLine("Question sent: " + question);
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

    /// <summary>
    /// The debugging script. It sends the question the same way the real one does, waits a while
    /// for an answer, and then writes down every visible piece of text with the marks that would
    /// let a person decide which one is the answer: what element it is, where it sits, how long it
    /// is, how many controls it holds, whether it is new, and whether the real reader would have
    /// taken it as a block.
    /// </summary>
    private static string DebugScript(string question)
    {
        string asked = JsonSerializer.Serialize(question);

        return """
        (async () => {
          const sleep = ms => new Promise(r => setTimeout(r, ms));
          const norm = s => (s || '').replace(/\s+/g, ' ').trim();
          const q1 = norm(__QUESTION__);

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

          const composer = () => deep().filter(el => {
            if (!visible(el)) return false;
            if (el.isContentEditable) return true;
            const tag = el.tagName;
            if (tag === 'TEXTAREA') return true;
            if (tag === 'INPUT' && (el.type === 'text' || el.type === 'search')) return true;
            return el.getAttribute && el.getAttribute('role') === 'textbox';
          }).pop();

          const record = (before) => {
            const items = [];
            for (const el of deep()) {
              if (!visible(el)) continue;
              let t; try { t = norm(el.innerText); } catch (e) { continue; }
              if (t.length < 2 || t.length > 4000) continue;
              if (!leaf(el, t)) continue;
              let controls = 0;
              try { controls = el.querySelectorAll('a,button,[role="button"],[role="link"],[role="tab"],[role="menuitem"],[role="option"]').length; } catch (e) {}
              const tag = el.tagName;
              const isControl = tag === 'BUTTON' || tag === 'A' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'NAV' || tag === 'LI';
              let inNav = false; try { inNav = !!(el.closest && el.closest('nav,[role="navigation"],[role="list"],[role="listbox"],header,footer')); } catch (e) {}
              const block = !isControl && !el.isContentEditable && !inNav && controls < 3;
              let box; try { box = el.getBoundingClientRect(); } catch (e) { box = { top: 0, height: 0 }; }
              items.push({
                tag: tag,
                role: (el.getAttribute && el.getAttribute('role')) || '-',
                testid: (el.getAttribute && (el.getAttribute('data-testid') || el.getAttribute('data-test-id'))) || '-',
                top: box.top | 0,
                h: box.height | 0,
                len: t.length,
                controls: controls,
                block: block,
                fresh: !before.has(t),
                text: t.slice(0, 160),
              });
            }
            return items;
          };

          const box = composer();
          if (!box) { return { error: 'no composer found', composer: false, url: location.href, items: record(new Set()) }; }

          const beforeItems = record(new Set());
          const before = new Set(beforeItems.map(i => i.text.length >= 160 ? i.text : norm(i.text)));

          // Send the question the same way the real run does.
          box.focus();
          if (box.isContentEditable) {
            try { document.execCommand('selectAll', false, null); } catch (e) {}
            if (!(() => { try { return document.execCommand('insertText', false, q1); } catch (e) { return false; } })()) box.textContent = q1;
          } else {
            const proto = box.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
            Object.getOwnPropertyDescriptor(proto, 'value').set.call(box, q1);
          }
          box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: q1 }));
          await sleep(600);
          for (const n of ['keydown','keypress','keyup']) box.dispatchEvent(new KeyboardEvent(n, { key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true }));
          await sleep(1800);

          const grew = () => record(before).some(i => i.fresh && i.len > q1.length + 4);
          if (!grew()) {
            const btn = deep().filter(b => {
              if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) return false;
              if (!visible(b) || b.disabled) return false;
              const l = ((b.getAttribute('aria-label')||'')+' '+(b.title||'')+' '+(b.textContent||'')+' '+(b.getAttribute('data-testid')||'')).toLowerCase();
              return /send|submit/.test(l);
            }).pop();
            if (btn) btn.click();
          }

          // Give the answer time, then hold still.
          let steady = ''; let still = 0; const until = Date.now() + 60000;
          while (Date.now() < until) {
            await sleep(1200);
            const now = JSON.stringify(record(before).filter(i => i.fresh).map(i => i.text));
            if (now === steady) { still++; if (still >= 3) break; } else still = 0;
            steady = now;
          }

          return { composer: true, url: location.href, items: record(before) };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }

    /// <summary>
    /// True once the browser is somewhere that is the chat itself rather than a step on the way
    /// to it. Sign in bounces through several Microsoft addresses, and every one of them would
    /// otherwise look like arrival.
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

    // ------------------------------------------------------------------ asking it something

    /// <summary>
    /// Puts one question to Copilot and gives back what it said. Each question starts a new chat,
    /// so nothing from the last step colours the next one, which is the same rule the Graph route
    /// follows for the same reason.
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
            await Session.GoAsync(work ? WorkPage : PersonalPage, token).ConfigureAwait(true);

            // If the page has bounced to a sign in, stop here. Carrying on would type the question
            // into whatever box the sign in page happens to have and read something back that is
            // not an answer, which is worse than a plain failure.
            if (!Landed(Session.Address, work))
            {
                throw new InvalidOperationException(
                    "Copilot is asking to be signed in to again. Open Settings and sign in to Copilot.");
            }

            JsonNode? answered = await Session
                .RunAsync(Script(question), token)
                .ConfigureAwait(true);

            return Read(answered);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns what the page returned into either an answer or a reason it failed. The reason
    /// names the stage that failed, so a report of what went wrong points at one place rather
    /// than at the whole thing.
    /// </summary>
    private static string Read(JsonNode? answered)
    {
        if (answered is null)
        {
            throw new InvalidOperationException(
                "The Copilot page did not answer at all, which usually means it is still loading or"
                + " has changed shape. Try again, or use the work account route or a key.");
        }

        string stage = answered["stage"]?.GetValue<string>() ?? string.Empty;
        string? failed = answered["error"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(failed))
        {
            string where = stage switch
            {
                "compose" => "Stepwright could not find the box to type the question into",
                "send" => "Stepwright typed the question but could not send it",
                "answer" => "Stepwright sent the question but could not read the answer",
                _ => "The Copilot page could not be used",
            };

            // A sample of what the page held is carried through, so a report of a failure to read
            // the answer shows the shape of the page rather than only that it failed.
            string sample = (answered["sample"]?.GetValue<string>() ?? string.Empty).Replace('\n', ' ').Trim();
            if (sample.Length > 160)
            {
                sample = sample[..160] + "...";
            }

            string tail = sample.Length > 0 ? " The page ended with: " + sample : string.Empty;

            throw new InvalidOperationException($"{where}. {failed}.{tail}");
        }

        string text = answered["text"]?.GetValue<string>() ?? string.Empty;

        return text.Trim().Length == 0
            ? throw new InvalidOperationException("Copilot answered but Stepwright could not read the answer off the page. Try again, or use a key.")
            : text.Trim();
    }

    /// <summary>
    /// The script that does the talking.
    ///
    /// The Copilot chat page is a heavy application that builds itself after it loads, hides parts
    /// of itself inside shadow roots and frames, and does not always send on the Enter key. So
    /// this waits for the box to actually exist before typing, looks for it through shadow roots
    /// and same origin frames rather than only at the top of the page, sends by key and then by
    /// button, and waits for the answer to stop growing. Every place it can give up names the
    /// stage it gave up at, so a failure says where it happened.
    /// </summary>
    private static string Script(string question)
    {
        string asked = JsonSerializer.Serialize(question);

        return """
        (async () => {
          const sleep = ms => new Promise(r => setTimeout(r, ms));
          const question = __QUESTION__;

          // Everything on the page, reached through shadow roots and same origin frames, not just
          // the top document. The chat surface lives inside these more often than not.
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
                if (el.tagName === 'IFRAME') {
                  try { if (el.contentDocument) stack.push(el.contentDocument); } catch (e) {}
                }
              }
            }
            return out;
          };

          const visible = el => {
            if (!el) return false;
            let box;
            try { box = el.getBoundingClientRect(); } catch (e) { return false; }
            if (box.width < 60 || box.height < 12) return false;
            const style = (el.ownerDocument.defaultView || window).getComputedStyle(el);
            return style && style.visibility !== 'hidden' && style.display !== 'none';
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
            // The composer sits at the bottom, so the lowest visible one on the page wins.
            boxes.sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);
            return boxes[boxes.length - 1] || null;
          };

          // The whole readable text on the page, reached the same deep way, so an answer written
          // inside a frame still counts.
          const transcript = () => {
            let text = '';
            const seen = new Set();
            const docs = [document];
            for (const el of deep()) {
              if (el.tagName === 'IFRAME') {
                try { if (el.contentDocument) docs.push(el.contentDocument); } catch (e) {}
              }
            }
            for (const d of docs) {
              if (seen.has(d)) continue;
              seen.add(d);
              try { text += '\n' + (d.body ? d.body.innerText : ''); } catch (e) {}
            }
            return text;
          };

          // 1. Wait for the box to exist. The page is still building itself for a while after it
          //    says it has loaded.
          let box = null;
          const appear = Date.now() + 30000;
          while (Date.now() < appear) {
            box = composer();
            if (box) break;
            await sleep(500);
          }

          if (!box) {
            return { stage: 'compose', error: 'The page may still be loading, or it keeps the chat inside a part this cannot reach.' };
          }

          const norm = s => (s || '').replace(/\s+/g, ' ').trim();
          const q1 = norm(question);

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

          // The readable blocks of the page: visible chunks of text that are not the composer, a
          // button or a menu, and that hold their own text rather than wrapping other blocks.
          // Reading these and taking only the ones that are new is what keeps the answer from
          // being lost among the suggestions, the sidebar and the person's own name, which is
          // exactly what went wrong before.
          const blocks = () => {
            const out = [];
            for (const el of deep()) {
              if (!visible(el)) continue;
              const tag = el.tagName;
              if (tag === 'BUTTON' || tag === 'A' || tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'NAV' || tag === 'LI') continue;
              if (el.isContentEditable) continue;
              try { if (el.closest && el.closest('nav,[role="navigation"],[role="list"],[role="listbox"],header,footer')) continue; } catch (e) {}
              // A thing made of links and buttons is a sidebar, a toolbar or a row of suggested
              // prompts, never an answer. An answer is prose and holds no controls of its own.
              let controls = 0;
              try { controls = el.querySelectorAll('a,button,[role="button"],[role="link"],[role="tab"],[role="menuitem"],[role="option"]').length; } catch (e) {}
              if (controls >= 3) continue;

              let t;
              try { t = norm(el.innerText); } catch (e) { continue; }
              if (t.length < 2 || t.length > 8000) continue;
              let wrapper = false;
              for (const c of el.children) {
                let ct = '';
                try { ct = norm(c.innerText); } catch (e) {}
                if (ct.length >= t.length * 0.9) { wrapper = true; break; }
              }
              if (wrapper) continue;
              out.push(t);
            }
            return out;
          };

          const isQuestion = t => t === q1 || (q1.length > 10 && t.indexOf(q1.slice(0, Math.min(30, q1.length))) === 0);

          // The answer is the largest block that is new since the question was sent and is not
          // the question read back. Largest, because a real answer is the substantial new thing
          // on the page and the leftover chrome that changes is small.
          const answerFrom = seen => {
            const fresh = blocks().filter(t => !seen.has(t) && !isQuestion(t));
            if (!fresh.length) return '';
            fresh.sort((a, b) => a.length - b.length);
            return stripChrome(fresh[fresh.length - 1]);
          };

          const before = new Set(blocks());

          box.focus();
          try { box.scrollIntoView({ block: 'center' }); } catch (e) {}

          // Typed as one line, so a newline is never read as a send and the question reads back
          // the same way it was sent.
          const type = () => {
            if (box.isContentEditable) {
              try { document.execCommand('selectAll', false, null); } catch (e) {}
              const ok = (() => { try { return document.execCommand('insertText', false, q1); } catch (e) { return false; } })();
              if (!ok) { box.textContent = q1; }
            } else {
              const proto = box.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
              setter.call(box, q1);
            }
            box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: q1 }));
          };

          type();
          await sleep(600);

          const press = name => box.dispatchEvent(new KeyboardEvent(name, {
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true,
          }));

          press('keydown'); press('keypress'); press('keyup');

          await sleep(1800);

          // Did the question go? A sent question shows up as a new block. If not, this page does
          // not send on Enter, so find the send button and click it.
          const sent = () => blocks().some(t => !before.has(t) && isQuestion(t));

          if (!sent()) {
            const button = deep().filter(b => {
              if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) return false;
              if (!visible(b) || b.disabled || b.getAttribute('aria-disabled') === 'true') return false;
              const label = ((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '') + ' ' + (b.getAttribute('data-testid') || '')).toLowerCase();
              return /send|submit/.test(label);
            }).pop();
            if (button) { button.click(); }
          }

          // Wait for the answer to appear and then hold still. An answer still being written keeps
          // changing; a finished one stops.
          let text = '';
          let steady = '';
          let still = 0;
          const until = Date.now() + 150000;

          while (Date.now() < until) {
            await sleep(1000);
            const now = answerFrom(before);
            if (now && now === steady) {
              still += 1;
              if (still >= 3) { text = now; break; }
            } else {
              still = 0;
            }
            steady = now;
          }

          if (!text) { text = answerFrom(before); }

          if (!text) {
            // Show the blocks that are new, so a report of this says what actually appeared rather
            // than the furniture around it.
            const fresh = blocks().filter(t => !before.has(t));
            return { stage: 'answer', error: 'nothing that reads as an answer appeared', sample: fresh.slice(-6).join(' | ').slice(0, 280) };
          }

          return { text: text };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }
}
