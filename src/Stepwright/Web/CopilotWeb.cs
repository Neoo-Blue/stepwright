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

            if (Session.Address.Contains("login.", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Copilot is asking to be signed in to again. Open Settings and sign in.");
            }

            JsonNode? answered = await Session
                .RunAsync(Script(question), token)
                .ConfigureAwait(true);

            return Read(answered);
        }).ConfigureAwait(false);
    }

    /// <summary>Turns what the page returned into either an answer or a reason it failed.</summary>
    private static string Read(JsonNode? answered)
    {
        if (answered is null)
        {
            throw new InvalidOperationException("The Copilot page did not answer. Try again, or use a key.");
        }

        string? failed = answered["error"]?.GetValue<string>();

        if (!string.IsNullOrWhiteSpace(failed))
        {
            throw new InvalidOperationException("Copilot could not be asked through the page. " + failed);
        }

        string text = answered["text"]?.GetValue<string>() ?? string.Empty;

        return text.Trim().Length == 0
            ? throw new InvalidOperationException("Copilot answered with nothing. Try again, or use a key.")
            : text.Trim();
    }

    /// <summary>
    /// The script that does the talking.
    ///
    /// It finds the box a person would type in, types, sends, and then waits for the page to stop
    /// changing rather than for any particular element to appear. Waiting for stillness is the
    /// one thing that stays true across redesigns: an answer that is still being written keeps
    /// changing the page, and one that is finished stops.
    /// </summary>
    private static string Script(string question)
    {
        string asked = JsonSerializer.Serialize(question);

        return """
        (async () => {
          const sleep = ms => new Promise(r => setTimeout(r, ms));
          const question = __QUESTION__;

          const visible = el => {
            if (!el) return false;
            const box = el.getBoundingClientRect();
            return box.width > 80 && box.height > 12;
          };

          // The composer is the last thing on the page a person could type into. Chat pages put
          // it at the bottom, and the transcript above it never contains one.
          const boxes = [...document.querySelectorAll('textarea, [contenteditable="true"], input[type="text"]')]
            .filter(visible);
          const box = boxes[boxes.length - 1];

          if (!box) {
            return { error: 'the box to type in could not be found on the page' };
          }

          const before = document.body.innerText || '';

          box.focus();
          box.scrollIntoView({ block: 'center' });

          if (box.isContentEditable) {
            document.execCommand('selectAll', false, null);
            document.execCommand('insertText', false, question);
          } else {
            const setter = Object.getOwnPropertyDescriptor(
              box.tagName === 'TEXTAREA' ? window.HTMLTextAreaElement.prototype : window.HTMLInputElement.prototype,
              'value').set;
            setter.call(box, question);
            box.dispatchEvent(new Event('input', { bubbles: true }));
          }

          await sleep(400);

          const key = name => box.dispatchEvent(new KeyboardEvent(name, {
            key: 'Enter', code: 'Enter', keyCode: 13, which: 13, bubbles: true, cancelable: true,
          }));

          key('keydown'); key('keypress'); key('keyup');

          await sleep(1200);

          // Nothing moved, so the Enter key was not what sends it here. Press the button instead.
          if ((document.body.innerText || '') === before) {
            const send = [...document.querySelectorAll('button')].filter(b => {
              const label = ((b.getAttribute('aria-label') || '') + ' ' + (b.title || '') + ' ' + (b.textContent || '')).toLowerCase();
              return visible(b) && !b.disabled && /send|submit/.test(label);
            }).pop();

            if (send) { send.click(); }
          }

          // Wait for the page to stop growing. Two and a half minutes is longer than Copilot has
          // ever needed and short enough that a wedged page does not hold up the whole run.
          let last = '';
          let still = 0;
          const until = Date.now() + 150000;

          while (Date.now() < until) {
            await sleep(900);
            const now = document.body.innerText || '';

            if (now === last && now.length > before.length + 8) {
              still += 1;
              if (still >= 4) { break; }
            } else {
              still = 0;
            }

            last = now;
          }

          const after = document.body.innerText || '';

          if (after.length <= before.length + 8) {
            return { error: 'the page never showed an answer' };
          }

          // The answer is whatever follows the question in reading order. Matching on the start
          // of the question rather than the whole of it survives a page that wraps or trims it.
          const needle = question.slice(0, 48);
          const at = after.lastIndexOf(needle);
          let text = at >= 0 ? after.slice(at + needle.length) : after.slice(before.length);

          // The furniture at the bottom of the page is not part of what Copilot said.
          const chrome = [
            /copilot can make mistakes[\s\S]*$/i,
            /ai-generated content may be incorrect[\s\S]*$/i,
            /message copilot[\s\S]*$/i,
            /ask me anything[\s\S]*$/i,
          ];

          for (const pattern of chrome) { text = text.replace(pattern, ''); }

          return { text: text.trim() };
        })()
        """.Replace("__QUESTION__", asked, StringComparison.Ordinal);
    }
}
