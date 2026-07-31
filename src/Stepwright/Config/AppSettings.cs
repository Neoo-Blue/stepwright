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

    /// <summary>Blank means any work account. A tenant identifier pins it to one organisation.</summary>
    public string AiTenant { get; set; } = string.Empty;

    public string AiRefreshProtected { get; set; } = string.Empty;
    public string AiAccessProtected { get; set; } = string.Empty;
    public DateTimeOffset AiAccessExpires { get; set; }

    /// <summary>Which organisation the sign in belongs to, and who it belongs to.</summary>
    public string AiTenantId { get; set; } = string.Empty;
    public string AiAccount { get; set; } = string.Empty;

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
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // A damaged settings file should never stop the app from starting.
        }

        return new AppSettings();
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

    public string GetAiKey() => Reveal(AiKeyProtected);

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

    public void SetHuduKey(string plainKey) => HuduKeyProtected = Protect(plainKey);

    public string GetHuduKey() => Reveal(HuduKeyProtected);

    public void SetConfluenceToken(string plainToken) => ConfluenceTokenProtected = Protect(plainToken);

    public string GetConfluenceToken() => Reveal(ConfluenceTokenProtected);

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
    public bool HasAiKey => !string.IsNullOrEmpty(AiKeyProtected);

    [JsonIgnore]
    public bool HasAiToken => !string.IsNullOrEmpty(AiTokenProtected);

    /// <summary>True when the assistant has something to sign in with, whatever the route is.</summary>
    [JsonIgnore]
    public bool CanAskAssistant => AiAuth?.ToLowerInvariant() switch
    {
        "cli" => true,
        "token" => HasAiToken,
        "microsoft" => HasMicrosoftSignIn,
        _ => HasAiKey || (AiBaseUrl ?? string.Empty).Contains("localhost", StringComparison.OrdinalIgnoreCase),
    };

    [JsonIgnore]
    public bool HasHudu => !string.IsNullOrWhiteSpace(HuduBaseUrl) && !string.IsNullOrEmpty(HuduKeyProtected);

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
