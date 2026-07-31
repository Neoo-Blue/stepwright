using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Stepwright.Web;

/// <summary>
/// A browser that belongs to Stepwright, kept signed in between runs.
///
/// This exists because of a plain fact about the services technicians actually use: some of them
/// have no way for an application to sign in. Hudu issues keys and nothing else. Microsoft's own
/// Copilot needs an application registered in a tenant and an administrator to approve it. In
/// both cases the person sitting in front of the machine can reach the thing perfectly well in
/// their own browser, and it is only the application that is locked out.
///
/// So the application borrows a browser. The person signs in once, in a real window, on the real
/// site, typing their password into the vendor's own page and nowhere else. What is kept is what
/// a browser keeps: a profile folder full of cookies, held under this Windows account. Stepwright
/// never sees the password, and there is no key, no secret and no registration anywhere.
///
/// The profile is separate for each service, so signing out of one leaves the others alone, and
/// nothing shares a cookie jar with the browser the person uses for their own work.
/// </summary>
public sealed class WebSession : IDisposable
{
    private readonly string _service;
    private CoreWebView2Environment? _environment;
    private WebView2? _view;
    private Form? _window;

    public WebSession(string service) => _service = Safe(service);

    /// <summary>Where this service's cookies live. One folder per service, under this account.</summary>
    public string Profile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stepwright",
        "browser",
        _service);

    /// <summary>True when there is a profile on disk, which means somebody has signed in before.</summary>
    public bool Remembered => Directory.Exists(Profile) && Directory.EnumerateFileSystemEntries(Profile).Any();

    /// <summary>
    /// The message to show when the browser component is missing. It ships with Windows 11 and
    /// with most of Windows 10, so this is rare, but a technician on a locked down build deserves
    /// a sentence rather than a stack trace.
    /// </summary>
    public const string Missing =
        "This needs the Microsoft Edge WebView2 runtime, which is part of Windows 11 and most of"
        + " Windows 10. Install it from microsoft.com/edge/webview2 and try again.";

    public static bool Available
    {
        get
        {
            try
            {
                return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch
            {
                return false;
            }
        }
    }

    // ------------------------------------------------------------------ the window

    /// <summary>
    /// Opens the site in a window the person can see and use, and hands back when they close it
    /// or when <paramref name="finished"/> says the sign in is done.
    ///
    /// It is deliberately a real window rather than something hidden. A sign in that happens out
    /// of sight is a sign in nobody can check, and this one may ask for a second factor, may show
    /// a consent page, and may need a tenant chosen. All of that is the person's to see.
    /// </summary>
    public async Task<bool> ShowAsync(
        IWin32Window? owner,
        string address,
        string title,
        Func<string, bool>? finished = null,
        int width = 1040,
        int height = 800)
    {
        await ReadyAsync().ConfigureAwait(true);

        bool done = false;

        _window = new Form
        {
            Text = title,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(width, height),
            MinimizeBox = false,
            ShowIcon = false,
        };

        _view!.Dock = DockStyle.Fill;
        _window.Controls.Add(_view);

        void Watch(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            string at = _view.Source?.ToString() ?? string.Empty;

            if (finished is not null && finished(at))
            {
                done = true;
                _window?.Close();
            }
        }

        _view.NavigationCompleted += Watch;
        _view.CoreWebView2.Navigate(address);

        try
        {
            _window.ShowDialog(owner);
        }
        finally
        {
            _view.NavigationCompleted -= Watch;

            // The view outlives the window on purpose: once the person has signed in, the rest
            // of the work happens through this same view without another window appearing.
            _window.Controls.Remove(_view);
            _window.Dispose();
            _window = null;
        }

        return done;
    }

    /// <summary>
    /// Brings the browser up without showing it, for the runs after the first one. Nothing here
    /// signs anybody in: if the profile has gone stale the page will simply be a sign in page,
    /// and the caller is expected to notice and ask.
    /// </summary>
    public async Task ReadyAsync()
    {
        if (_view is not null)
        {
            return;
        }

        if (!Available)
        {
            throw new InvalidOperationException(Missing);
        }

        Directory.CreateDirectory(Profile);

        _environment = await CoreWebView2Environment
            .CreateAsync(browserExecutableFolder: null, userDataFolder: Profile)
            .ConfigureAwait(true);

        _view = new WebView2();
        await _view.EnsureCoreWebView2Async(_environment).ConfigureAwait(true);
    }

    /// <summary>Goes to a page and waits until it has finished loading.</summary>
    public async Task GoAsync(string address, CancellationToken token)
    {
        await ReadyAsync().ConfigureAwait(true);

        var arrived = new TaskCompletionSource<bool>();

        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs e) => arrived.TrySetResult(e.IsSuccess);

        _view!.NavigationCompleted += Done;

        try
        {
            _view.CoreWebView2.Navigate(address);

            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));

            await arrived.Task.WaitAsync(limit.Token).ConfigureAwait(true);
        }
        finally
        {
            _view.NavigationCompleted -= Done;
        }
    }

    /// <summary>
    /// Runs a piece of script in the page and gives back what it returned. Everything the page
    /// route does goes through here, because a request made by the page carries the person's own
    /// session without this app ever holding a token.
    /// </summary>
    public async Task<JsonNode?> RunAsync(string script, CancellationToken token)
    {
        await ReadyAsync().ConfigureAwait(true);

        Task<string> asked = _view!.CoreWebView2.ExecuteScriptAsync(script);

        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromMinutes(3));

        string raw = await asked.WaitAsync(limit.Token).ConfigureAwait(true);

        try
        {
            return JsonNode.Parse(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The text a piece of script returned, or nothing when it returned nothing.</summary>
    public async Task<string> TextAsync(string script, CancellationToken token)
    {
        JsonNode? answer = await RunAsync(script, token).ConfigureAwait(true);

        return answer switch
        {
            null => string.Empty,
            JsonValue value when value.TryGetValue(out string? text) => text ?? string.Empty,
            _ => answer.ToJsonString(),
        };
    }

    /// <summary>Where the browser currently is, which is how a sign in page is spotted.</summary>
    public string Address => _view?.Source?.ToString() ?? string.Empty;

    // ------------------------------------------------------------------ forgetting

    /// <summary>Signs out by throwing the profile away. There is nothing else to revoke.</summary>
    public void Forget()
    {
        Dispose();

        try
        {
            if (Directory.Exists(Profile))
            {
                Directory.Delete(Profile, recursive: true);
            }
        }
        catch (IOException)
        {
            // A profile the browser process still holds open is cleared on the next run instead.
        }
    }

    public void Dispose()
    {
        _view?.Dispose();
        _view = null;
        _window?.Dispose();
        _window = null;
        _environment = null;
    }

    private static string Safe(string name)
    {
        var clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        return clean.Length == 0 ? "service" : clean.ToLowerInvariant();
    }
}
