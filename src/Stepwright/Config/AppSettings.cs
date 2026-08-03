using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stepwright.Config;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Author { get; set; } = Environment.UserName;

    // Capture
    public bool CaptureAllMonitors { get; set; }
    public bool CaptureKeyboard { get; set; } = true;
    public bool CaptureScroll { get; set; } = true;
    public bool CaptureDrag { get; set; } = true;
    public bool HideAppFromCaptures { get; set; } = true;
    public int CountdownSeconds { get; set; } = 3;

    // Presentation
    public bool AutoZoom { get; set; } = true;
    public int ZoomPadding { get; set; } = 260;
    public bool ShowClickMarker { get; set; } = true;
    public bool ShowElementOutline { get; set; } = true;
    public string MarkerColor { get; set; } = "FF3B30";
    public bool AddHeadingOnAppChange { get; set; }
    public bool DarkTheme { get; set; } = true;

    // Text
    public int TypingMergeMilliseconds { get; set; } = 1400;
    public bool RedactPasswords { get; set; } = true;
    public List<string> RedactPatterns { get; set; } = new();

    // Shortcuts, stored as virtual key codes.
    public int HotkeyStartPause { get; set; } = 0x78; // F9
    public int HotkeyStop { get; set; } = 0x79;       // F10
    public int HotkeyShot { get; set; } = 0x77;       // F8

    /// <summary>When true the shortcuts also need Ctrl and Shift, which avoids clashes with other tools.</summary>
    public bool HotkeyNeedsModifiers { get; set; }

    // Optional writing assistant.
    public bool AiEnabled { get; set; }

    /// <summary>One of the identifiers in <see cref="AiProviders"/>.</summary>
    public string AiProvider { get; set; } = "openai";

    public string AiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string AiModel { get; set; } = "gpt-4o-mini";
    public string AiKeyProtected { get; set; } = string.Empty;

    /// <summary>
    /// How the assistant signs in. One of the values in AiAuthKinds: a key billed by the token,
    /// the command line app you already signed in to, or a subscription token sent direct.
    /// </summary>
    public string AiAuth { get; set; } = "key";

    /// <summary>Where the signed in command line app lives, when it is somewhere unusual.</summary>
    public string AiCliPath { get; set; } = string.Empty;

    /// <summary>A subscription token, encrypted the same way a key is.</summary>
    public string AiTokenProtected { get; set; } = string.Empty;

    /// <summary>The Microsoft application this app signs in through, registered in your tenant.</summary>
    public string AiAppId { get; set; } = string.Empty;

    /// <summary>
    /// Which Copilot the browser route opens: the one that comes with a work or school account,
    /// or the personal one. They are different products behind the same name.
    /// </summary>
    public bool AiCopilotWork { get; set; } = true;

    /// <summary>Blank means any work account. A tenant identifier pins it to one organisation.</summary>
    public string AiTenant { get; set; } = string.Empty;

    public string AiRefreshProtected { get; set; } = string.Empty;
    public string AiAccessProtected { get; set; } = string.Empty;
    public DateTimeOffset AiAccessExpires { get; set; }

    /// <summary>Which organisation the sign in belongs to, and who it belongs to.</summary>
    public string AiTenantId { get; set; } = string.Empty;
    public string AiAccount { get; set; } = string.Empty;

    /// <summary>The ChatGPT workspace a subscription sign in belongs to, sent on every request.</summary>
    public string AiWorkspace { get; set; } = string.Empty;

    /// <summary>The Cloud project a Gemini plan bills against, discovered once after sign in.</summary>
    public string AiProject { get; set; } = string.Empty;

    /// <summary>Which plan or tier is paying, kept only so the settings page can name it.</summary>
    public string AiPlan { get; set; } = string.Empty;

    /// <summary>
    /// Lets the assistant look at the screenshot for each step, which is what makes it able
    /// to name what is actually on screen. Off by default, because pictures leaving the
    /// machine is a decision the person has to make on purpose.
    /// </summary>
    public bool AiSendScreenshots { get; set; }

    /// <summary>Ask the assistant to add a short note under a step where one genuinely helps.</summary>
    public bool AiWriteNotes { get; set; } = true;

    /// <summary>How lively the movement in an animated step is: gentle, normal or quick.</summary>
    public string GifMotion { get; set; } = "Normal";

    /// <summary>Widest an animation is written, in pixels. Smaller keeps the file light.</summary>
    public int GifWidth { get; set; } = 760;

    /// <summary>Name of the format used when writing a document. See FormatProfiles.</summary>
    public string ExportFormat { get; set; } = "Stepwright";

    // Publishing straight into a knowledge base.
    public string HuduBaseUrl { get; set; } = string.Empty;
    public string HuduKeyProtected { get; set; } = string.Empty;
    public string HuduFormat { get; set; } = "Hudu";

    /// <summary>
    /// How a guide reaches Hudu. "key" uses the API and an administrator minted key, which is
    /// reliable. "web" drives the Hudu web page in a signed in browser and needs no key at all,
    /// for a technician who cannot mint one, at the cost of being the more fragile of the two.
    /// </summary>
    public string HuduPublish { get; set; } = "key";

    public string ConfluenceSite { get; set; } = string.Empty;
    public string ConfluenceEmail { get; set; } = string.Empty;
    public string ConfluenceTokenProtected { get; set; } = string.Empty;
    public string ConfluenceFormat { get; set; } = "Confluence";

    /// <summary>Either an email address with an API token, or a sign in through the browser.</summary>
    public string ConfluenceAuth { get; set; } = "token";

    /// <summary>The application you registered with Atlassian. The secret is encrypted.</summary>
    public string ConfluenceClientId { get; set; } = string.Empty;
    public string ConfluenceClientSecretProtected { get; set; } = string.Empty;

    public string ConfluenceRefreshProtected { get; set; } = string.Empty;
    public string ConfluenceAccessProtected { get; set; } = string.Empty;
    public DateTimeOffset ConfluenceAccessExpires { get; set; }
    public string ConfluenceCloudId { get; set; } = string.Empty;
    public string ConfluenceSiteName { get; set; } = string.Empty;

    public string LibraryFolder { get; set; } = DefaultLibraryFolder;

    [JsonIgnore]
    public static string DefaultLibraryFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Stepwright");

    [JsonIgnore]
    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stepwright");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        AppSettings settings = new();

        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);

                if (loaded is not null)
                {
                    settings = loaded;
                }
            }
        }
        catch
        {
            // A damaged settings file should never stop the app from starting.
        }

        // What an administrator decided comes last, so it wins over anything in the person's own
        // file, including a value they had before the policy arrived.
        settings.ApplyPolicy();
        return settings;
    }

    /// <summary>
    /// Lays the machine's policy over these settings. A locked policy replaces what is there and
    /// the settings page will not let it be edited. An unlocked one only fills in what the person
    /// has not set, which is how a company gives a starting point without taking the choice away.
    /// </summary>
    public void ApplyPolicy()
    {
        Policy policy = Policy.Current;

        if (!policy.Exists)
        {
            return;
        }

        bool Take(string? value, string current) =>
            !string.IsNullOrWhiteSpace(value) && (policy.Locked || string.IsNullOrWhiteSpace(current));

        if (Take(policy.AiProvider, AiProvider)) { AiProvider = policy.AiProvider!.Trim(); }
        if (Take(policy.AiAuth, AiAuth)) { AiAuth = policy.AiAuth!.Trim(); }
        if (Take(policy.AiBaseUrl, AiBaseUrl)) { AiBaseUrl = policy.AiBaseUrl!.Trim(); }
        if (Take(policy.AiModel, AiModel)) { AiModel = policy.AiModel!.Trim(); }
        if (Take(policy.AiAppId, AiAppId)) { AiAppId = policy.AiAppId!.Trim(); }
        if (Take(policy.AiTenant, AiTenant)) { AiTenant = policy.AiTenant!.Trim(); }

        if (Take(policy.HuduBaseUrl, HuduBaseUrl)) { HuduBaseUrl = policy.HuduBaseUrl!.Trim(); }
        if (Take(policy.HuduPublish, HuduPublish)) { HuduPublish = policy.HuduPublish!.Trim(); }
        if (Take(policy.ConfluenceSite, ConfluenceSite)) { ConfluenceSite = policy.ConfluenceSite!.Trim(); }
        if (Take(policy.ConfluenceEmail, ConfluenceEmail)) { ConfluenceEmail = policy.ConfluenceEmail!.Trim(); }
        if (Take(policy.ConfluenceAuth, ConfluenceAuth)) { ConfluenceAuth = policy.ConfluenceAuth!.Trim(); }
        if (Take(policy.LibraryFolder, LibraryFolder)) { LibraryFolder = policy.LibraryFolder!.Trim(); }

        // A key from a policy is deliberately not copied into the person's own settings file. It
        // stays where the administrator put it, and is unsealed only when a request is about to be
        // made, so it never lands anywhere a person could read it.
        if (!string.IsNullOrWhiteSpace(policy.AiKeyProtected)) { AiKeyProtected = string.Empty; }
        if (!string.IsNullOrWhiteSpace(policy.HuduKeyProtected)) { HuduKeyProtected = string.Empty; }
        if (!string.IsNullOrWhiteSpace(policy.ConfluenceTokenProtected)) { ConfluenceTokenProtected = string.Empty; }

        AiEnabled = AiEnabled || !string.IsNullOrWhiteSpace(policy.AiKeyProtected);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Read only profiles simply keep the defaults for the next run.
        }
    }

    /// <summary>Encrypts a secret for the current Windows account only.</summary>
    public static string Protect(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plain),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string Reveal(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void SetAiKey(string plainKey) => AiKeyProtected = Protect(plainKey);

    /// <summary>
    /// The key to use. A key an administrator sealed into the machine's policy is preferred and
    /// is unsealed only here, at the moment it is needed, so it never sits in the person's own
    /// settings file and is never handed to the settings page to display.
    /// </summary>
    public string GetAiKey() => Policy.Current.AiKeyProtected is { Length: > 0 } sealedKey
        ? Policy.Reveal(sealedKey)
        : Reveal(AiKeyProtected);

    public void SetAiToken(string plainToken) => AiTokenProtected = Protect(plainToken);

    public string GetAiToken() => Reveal(AiTokenProtected);

    public string GetAiRefresh() => Reveal(AiRefreshProtected);

    public string GetAiAccess() => Reveal(AiAccessProtected);

    /// <summary>Keeps what a finished Microsoft sign in handed back, so it survives a restart.</summary>
    public void RememberMicrosoft(Ai.MicrosoftSession session)
    {
        AiAccessProtected = Protect(session.AccessToken);
        AiRefreshProtected = Protect(session.RefreshToken);
        AiAccessExpires = session.Expires;

        if (!string.IsNullOrWhiteSpace(session.TenantId))
        {
            AiTenantId = session.TenantId;
        }

        if (!string.IsNullOrWhiteSpace(session.Account))
        {
            AiAccount = session.Account;
        }
    }

    public void ForgetMicrosoft()
    {
        AiAccessProtected = string.Empty;
        AiRefreshProtected = string.Empty;
        AiAccessExpires = default;
        AiTenantId = string.Empty;
        AiAccount = string.Empty;
    }

    [JsonIgnore]
    public bool HasMicrosoftSignIn => !string.IsNullOrEmpty(AiRefreshProtected);

    /// <summary>
    /// Keeps what a finished Claude sign in handed back. It shares the fields the Microsoft sign
    /// in uses, because the assistant is signed in to one service at a time and two sets of
    /// fields would only make it possible for them to disagree.
    /// </summary>
    public void RememberClaude(Ai.ClaudeSession session)
    {
        AiAccessProtected = Protect(session.AccessToken);
        AiRefreshProtected = Protect(session.RefreshToken);
        AiAccessExpires = session.Expires;

        if (!string.IsNullOrWhiteSpace(session.Account))
        {
            AiAccount = session.Account;
        }
    }

    public void ForgetClaude() => ForgetSubscription();

    [JsonIgnore]
    public bool HasClaudeSignIn => !string.IsNullOrEmpty(AiRefreshProtected);

    /// <summary>
    /// Keeps what a finished ChatGPT sign in handed back. It shares the same fields as the other
    /// sign ins, because the assistant is signed in to one service at a time.
    /// </summary>
    public void RememberChatGpt(Ai.ChatGptSession session)
    {
        AiAccessProtected = Protect(session.AccessToken);
        AiRefreshProtected = Protect(session.RefreshToken);
        AiAccessExpires = session.Expires;
        AiWorkspace = session.Workspace;
        AiPlan = session.Plan;

        if (!string.IsNullOrWhiteSpace(session.Account))
        {
            AiAccount = session.Account;
        }
    }

    /// <summary>Keeps what a finished Gemini sign in handed back.</summary>
    public void RememberGemini(Ai.GeminiSession session)
    {
        AiAccessProtected = Protect(session.AccessToken);
        AiRefreshProtected = Protect(session.RefreshToken);
        AiAccessExpires = session.Expires;
        AiProject = session.Project;
        AiPlan = session.Plan;

        if (!string.IsNullOrWhiteSpace(session.Account))
        {
            AiAccount = session.Account;
        }
    }

    /// <summary>Clears whatever native sign in is held. There is only ever one.</summary>
    public void ForgetSubscription()
    {
        AiAccessProtected = string.Empty;
        AiRefreshProtected = string.Empty;
        AiAccessExpires = default;
        AiAccount = string.Empty;
        AiWorkspace = string.Empty;
        AiProject = string.Empty;
        AiPlan = string.Empty;
    }

    /// <summary>True when a native subscription sign in is held, whichever service it is for.</summary>
    [JsonIgnore]
    public bool HasSubscriptionSignIn => !string.IsNullOrEmpty(AiRefreshProtected);

    public void SetHuduKey(string plainKey) => HuduKeyProtected = Protect(plainKey);

    public string GetHuduKey() => Policy.Current.HuduKeyProtected is { Length: > 0 } sealedKey
        ? Policy.Reveal(sealedKey)
        : Reveal(HuduKeyProtected);

    public void SetConfluenceToken(string plainToken) => ConfluenceTokenProtected = Protect(plainToken);

    public string GetConfluenceToken() => Policy.Current.ConfluenceTokenProtected is { Length: > 0 } sealedToken
        ? Policy.Reveal(sealedToken)
        : Reveal(ConfluenceTokenProtected);

    public void SetConfluenceSecret(string plainSecret) => ConfluenceClientSecretProtected = Protect(plainSecret);

    public string GetConfluenceSecret() => Reveal(ConfluenceClientSecretProtected);

    public string GetConfluenceRefresh() => Reveal(ConfluenceRefreshProtected);

    public string GetConfluenceAccess() => Reveal(ConfluenceAccessProtected);

    /// <summary>Keeps what a finished sign in handed back, so it survives a restart.</summary>
    public void RememberConfluence(Publish.AtlassianSession session)
    {
        ConfluenceAccessProtected = Protect(session.AccessToken);
        ConfluenceRefreshProtected = Protect(session.RefreshToken);
        ConfluenceAccessExpires = session.Expires;
        ConfluenceCloudId = session.CloudId;
        ConfluenceSiteName = session.SiteName;

        if (!string.IsNullOrWhiteSpace(session.SiteUrl))
        {
            ConfluenceSite = session.SiteUrl;
        }
    }

    public void ForgetConfluence()
    {
        ConfluenceAccessProtected = string.Empty;
        ConfluenceRefreshProtected = string.Empty;
        ConfluenceAccessExpires = default;
        ConfluenceCloudId = string.Empty;
        ConfluenceSiteName = string.Empty;
    }

    [JsonIgnore]
    public bool HasAiKey =>
        !string.IsNullOrEmpty(AiKeyProtected) || !string.IsNullOrEmpty(Policy.Current.AiKeyProtected);

    [JsonIgnore]
    public bool HasAiToken => !string.IsNullOrEmpty(AiTokenProtected);

    /// <summary>True when the assistant has something to sign in with, whatever the route is.</summary>
    [JsonIgnore]
    public bool CanAskAssistant => AiAuth?.ToLowerInvariant() switch
    {
        "cli" => true,
        "token" => HasAiToken,
        "subscription" => HasClaudeSignIn,
        "browser" => true,
        "microsoft" => HasMicrosoftSignIn,
        _ => HasAiKey || (AiBaseUrl ?? string.Empty).Contains("localhost", StringComparison.OrdinalIgnoreCase),
    };

    [JsonIgnore]
    public bool HasHudu => !string.IsNullOrWhiteSpace(HuduBaseUrl)
        && (!string.IsNullOrEmpty(HuduKeyProtected) || !string.IsNullOrEmpty(Policy.Current.HuduKeyProtected));

    /// <summary>True when Hudu is set to publish by driving its web page rather than by the key.</summary>
    [JsonIgnore]
    public bool HuduUsesWeb => string.Equals(HuduPublish, "web", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when Hudu can publish at all, by whichever route it is set to.</summary>
    [JsonIgnore]
    public bool CanPublishHudu => HuduUsesWeb
        ? !string.IsNullOrWhiteSpace(HuduBaseUrl)
        : HasHudu;

    [JsonIgnore]
    public bool ConfluenceUsesOAuth =>
        string.Equals(ConfluenceAuth, "oauth", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasConfluenceSignIn =>
        !string.IsNullOrEmpty(ConfluenceRefreshProtected) && !string.IsNullOrWhiteSpace(ConfluenceCloudId);

    [JsonIgnore]
    public bool HasConfluence => ConfluenceUsesOAuth
        ? HasConfluenceSignIn
        : !string.IsNullOrWhiteSpace(ConfluenceSite)
          && !string.IsNullOrWhiteSpace(ConfluenceEmail)
          && !string.IsNullOrEmpty(ConfluenceTokenProtected);
}
