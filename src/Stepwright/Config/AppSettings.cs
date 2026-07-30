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

    public void SetHuduKey(string plainKey) => HuduKeyProtected = Protect(plainKey);

    public string GetHuduKey() => Reveal(HuduKeyProtected);

    public void SetConfluenceToken(string plainToken) => ConfluenceTokenProtected = Protect(plainToken);

    public string GetConfluenceToken() => Reveal(ConfluenceTokenProtected);

    [JsonIgnore]
    public bool HasAiKey => !string.IsNullOrEmpty(AiKeyProtected);

    [JsonIgnore]
    public bool HasHudu => !string.IsNullOrWhiteSpace(HuduBaseUrl) && !string.IsNullOrEmpty(HuduKeyProtected);

    [JsonIgnore]
    public bool HasConfluence =>
        !string.IsNullOrWhiteSpace(ConfluenceSite)
        && !string.IsNullOrWhiteSpace(ConfluenceEmail)
        && !string.IsNullOrEmpty(ConfluenceTokenProtected);
}
