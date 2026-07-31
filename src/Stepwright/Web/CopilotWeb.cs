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

          const before = transcript();

          box.focus();
          try { box.scrollIntoView({ block: 'center' }); } catch (e) {}

          const type = () => {
            if (box.isContentEditable) {
              try { document.execCommand('selectAll', false, null); } catch (e) {}
              const ok = (() => { try { return document.execCommand('insertText', false, question); } catch (e) { return false; } })();
              if (!ok) {
                box.textContent = question;
              }
            } else {
              const proto = box.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype;
              const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
              setter.call(box, question);
            }
            box.dispatchEvent(new InputEvent('input', { bubbles: true, inputType: 'insertText', data: question }));
          };

          type();
          await sleep(500);

          const key = name => box.dispatchEvent(new KeyboardEvent(name, {
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true,
          }));

          key('keydown'); key('keypress'); key('keyup');

          await sleep(1500);

          // Nothing moved, so this page does not send on Enter. Find the send button, which sits
          // right by the box and is enabled only once there is something to send.
          const grew = () => transcript().length > before.length + 8;

          if (!grew()) {
            const send = deep().filter(b => {
              if (b.tagName !== 'BUTTON' && !(b.getAttribute && b.getAttribute('role') === 'button')) return false;
              if (!visible(b) || b.disabled || b.getAttribute('aria-disabled') === 'true') return false;
              const label = ((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '') + ' ' + (b.getAttribute('data-testid') || '')).toLowerCase();
              return /send|submit/.test(label);
            }).pop();

            if (send) {
              send.click();
            } else {
              return { stage: 'send', error: 'The Enter key did nothing and no send button could be found.' };
            }
          }

          await sleep(1500);

          // 2. Wait for the answer to appear and then to stop growing. An answer still being
          //    written keeps the page changing; a finished one goes still.
          let last = '';
          let still = 0;
          const until = Date.now() + 150000;

          while (Date.now() < until) {
            await sleep(900);
            const now = transcript();

            if (now === last && now.length > before.length + 8) {
              still += 1;
              if (still >= 4) break;
            } else {
              still = 0;
            }

            last = now;
          }

          let after = transcript();

          if (after.length <= before.length + 8) {
            return { stage: 'answer', error: 'The question was sent but nothing new appeared within the wait.' };
          }

          const strip = s => {
            const chrome = [
              /copilot can make mistakes[\s\S]*$/i,
              /ai-generated content may be incorrect[\s\S]*$/i,
              /message copilot[\s\S]*$/i,
              /ask me anything[\s\S]*$/i,
            ];
            for (const p of chrome) s = s.replace(p, '');
            return s.trim();
          };

          const needle = question.slice(0, Math.min(48, question.length));
          const shortNeedle = question.slice(0, Math.min(24, question.length));

          // The composer very often keeps the question sitting at the bottom of the page after
          // it is sent, so an occurrence of it at the very end is that box, not a turn in the
          // transcript. Cut a trailing copy off before looking for the answer.
          const trailing = after.lastIndexOf(shortNeedle);
          if (trailing > after.length - question.length - 80) {
            after = after.slice(0, trailing);
          }

          // The answer is the text that follows the most recent real occurrence of the question.
          // Walk occurrences from the last backwards and take the first that leaves something.
          const spots = [];
          let idx = after.indexOf(needle);
          while (idx !== -1) { spots.push(idx); idx = after.indexOf(needle, idx + 1); }

          let text = '';
          for (let k = spots.length - 1; k >= 0; k--) {
            const candidate = strip(after.slice(spots[k] + needle.length));
            if (candidate.length > 0) { text = candidate; break; }
          }

          // Nothing keyed off the question. Fall back to whatever is new since before it was sent.
          if (!text && after.length > before.length) {
            text = strip(after.slice(before.length));
          }

          if (!text) {
            // Hand back a look at the tail of the page, so a report of this says what the page
            // actually held where the answer was expected.
            return { stage: 'answer', error: 'new text appeared but the answer could not be told apart from it', sample: after.slice(-280) };
          }

          return { text: text };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }
}
