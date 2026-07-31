namespace Stepwright.Web;

/// <summary>
/// Hudu, signed in to rather than configured.
///
/// Hudu has no way for an application to sign in. Its API takes a key an administrator mints in
/// the admin area, and that is the whole of it, so the honest version of "as easy as sign in" for
/// Hudu is this: open Hudu in a window, let the person sign in the way they always do, take them
/// straight to the page where keys live, and then read the key off that page the moment it
/// appears rather than asking them to copy it across.
///
/// What this does not do is pretend the key is unnecessary. It is Hudu's design, it grants
/// access to every company on the instance, and Stepwright says so on the settings page. What is
/// removed is the part that was busywork: finding the page, and carrying a secret between two
/// windows by hand.
/// </summary>
public static class HuduWeb
{
    private static WebSession? _session;

    public static WebSession Session => _session ??= new WebSession("hudu");

    /// <summary>True when somebody has signed in to Hudu on this machine before.</summary>
    public static bool Remembered => Session.Remembered;

    /// <summary>
    /// Opens Hudu at the page where keys live and waits. The window stays open until the person
    /// closes it, because only they know when the key has actually been created.
    /// </summary>
    public static async Task<string> SignInAsync(IWin32Window? owner, string site, CancellationToken token)
    {
        string address = Address(site);

        return await UiThread.RunAsync(async () =>
        {
            await Session
                .ShowAsync(owner, address, "Sign in to Hudu, then create a key")
                .ConfigureAwait(true);

            // The window has closed, and the view it used is still alive and still on that page,
            // so whatever the person created is still there to be read.
            return await FoundAsync(token).ConfigureAwait(true);
        }).ConfigureAwait(false);
    }

    public static void SignOut()
    {
        _session?.Forget();
        _session = null;
    }

    // ------------------------------------------------------------------ publishing with no key

    /// <summary>
    /// Publishes a guide by filling in the Hudu web page rather than by calling the API, so a
    /// technician who cannot mint a key can still get a guide into Hudu.
    ///
    /// It fills, it does not save. Stepwright opens Hudu, waits until the person has the new
    /// article page open in front of them, writes the title and the guide into it, and then steps
    /// back so the person can look it over and press Save themselves. The choosing of the company,
    /// the review, and the save are all left to the person, because those are the acts a wrong
    /// guess would do harm with, and because without a key there is no list of companies to choose
    /// from safely on their behalf.
    /// </summary>
    public static async Task<string> PublishAsync(
        string site,
        string title,
        string html,
        Action<string>? note,
        CancellationToken token)
    {
        if (!Session.Remembered)
        {
            throw new InvalidOperationException(
                "Hudu is not signed in on this machine yet. Sign in to Hudu in Settings first.");
        }

        string start = Home(site);

        note?.Invoke(
            "Opening Hudu. Go to the company you want, open its Knowledge Base, and start a new"
            + " article. Stepwright fills it in as soon as the editor is on screen.");

        string landed = await UiThread.RunAsync(async () =>
            await Session
                .AssistAsync(null, start, "Publish to Hudu", Fill(title, html), note, token)
                .ConfigureAwait(true)).ConfigureAwait(false);

        // The address the window was left on is the article, when the person saved it, or the
        // editor, when they did not. Either way it is the most useful thing to hand back.
        return landed;
    }

    private static string Home(string site)
    {
        string clean = (site ?? string.Empty).Trim().TrimEnd('/');

        if (clean.Length == 0)
        {
            throw new InvalidOperationException("Fill in the address of your Hudu site first.");
        }

        return clean.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? clean : "https://" + clean;
    }

    /// <summary>
    /// The fill script. It looks for the article's name field and its editor, and when both are
    /// there it writes the title and loads the guide in. It knows the three editors Hudu and apps
    /// like it use: Trix, which is the Rails default and takes html through its own editor, a
    /// TinyMCE editor, which has a set content call, and a plain rich text box, which takes html
    /// through the browser's own insert. Until the editor is on screen it simply reports that it
    /// has not filled anything yet, and is asked again a second later.
    /// </summary>
    private static string Fill(string title, string html)
    {
        string t = System.Text.Json.JsonSerializer.Serialize(title);
        string h = System.Text.Json.JsonSerializer.Serialize(html);

        return """
        (() => {
          const title = __TITLE__;
          const html = __HTML__;

          const visible = el => {
            if (!el) return false;
            let b; try { b = el.getBoundingClientRect(); } catch (e) { return false; }
            return b.width > 40 && b.height > 8;
          };

          // The name field. Hudu calls an article's title its Name, so match either word, and
          // fall back to the first visible text box near the top of the form.
          const named = [...document.querySelectorAll('input[type="text"], input:not([type])')].filter(visible);
          let nameBox = named.find(i => {
            const s = ((i.name || '') + ' ' + (i.id || '') + ' ' + (i.placeholder || '') + ' ' + (i.getAttribute('aria-label') || '')).toLowerCase();
            return /name|title/.test(s);
          }) || named[0];

          // The editor, in the order these apps use.
          const trix = document.querySelector('trix-editor');
          const tiny = window.tinymce && window.tinymce.activeEditor;
          const ce = [...document.querySelectorAll('[contenteditable="true"]')].filter(visible)[0];

          if (!nameBox && !trix && !tiny && !ce) {
            return { filled: false };
          }

          if (nameBox && !nameBox.value) {
            const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value').set;
            setter.call(nameBox, title);
            nameBox.dispatchEvent(new Event('input', { bubbles: true }));
          }

          let where = null;

          if (trix && trix.editor) {
            trix.editor.loadHTML(html);
            where = 'trix';
          } else if (tiny) {
            tiny.setContent(html);
            where = 'tinymce';
          } else if (ce) {
            ce.focus();
            try { document.execCommand('selectAll', false, null); } catch (e) {}
            const ok = (() => { try { return document.execCommand('insertHTML', false, html); } catch (e) { return false; } })();
            if (!ok) { ce.innerHTML = html; }
            ce.dispatchEvent(new Event('input', { bubbles: true }));
            where = 'editor';
          }

          // The title is not enough on its own. Wait for the editor before calling it filled, so
          // a page that shows the name box a moment before the editor is not declared done early.
          if (!where) {
            return { filled: false };
          }

          return { filled: true, editor: where };
        })()
        """
        .Replace("__TITLE__", t, StringComparison.Ordinal)
        .Replace("__HTML__", h, StringComparison.Ordinal);
    }

    /// <summary>The admin page where Hudu keeps its keys, built from the site's own address.</summary>
    public static string Address(string site)
    {
        string clean = (site ?? string.Empty).Trim().TrimEnd('/');

        if (clean.Length == 0)
        {
            throw new InvalidOperationException(
                "Fill in the address of your Hudu site first, so there is somewhere to sign in to.");
        }

        if (!clean.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            clean = "https://" + clean;
        }

        return clean + "/admin/api_keys";
    }

    /// <summary>
    /// Looks for a key on the page. Hudu shows a new key once, in full, and a key is long,
    /// unbroken and unlike anything else on an admin page, so it can be found by shape without
    /// depending on what the page calls it this year.
    /// </summary>
    private static Task<string> FoundAsync(CancellationToken token) => Session.TextAsync(
        """
        (() => {
          const seen = new Set();

          // Anywhere a key could be shown: a box holding it, or plain text on the page.
          for (const el of document.querySelectorAll('input, textarea, code, pre, td, span, div')) {
            const text = ((el.value !== undefined ? el.value : el.textContent) || '').trim();

            if (text.length >= 24 && text.length <= 96 && /^[A-Za-z0-9_-]+$/.test(text) && /[0-9]/.test(text)) {
              seen.add(text);
            }
          }

          // The longest candidate, because the short ones are identifiers and page furniture.
          return [...seen].sort((a, b) => b.length - a.length)[0] || '';
        })()
        """,
        token);
}
