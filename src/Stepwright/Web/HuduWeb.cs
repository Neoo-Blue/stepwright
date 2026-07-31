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
