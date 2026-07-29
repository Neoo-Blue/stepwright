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

    /// <summary>Stores the key encrypted for the current Windows account only.</summary>
    public void SetAiKey(string plainKey)
    {
        if (string.IsNullOrWhiteSpace(plainKey))
        {
            AiKeyProtected = string.Empty;
            return;
        }

        try
        {
            byte[] cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plainKey),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            AiKeyProtected = Convert.ToBase64String(cipher);
        }
        catch
        {
            AiKeyProtected = string.Empty;
        }
    }

    public string GetAiKey()
    {
        if (string.IsNullOrEmpty(AiKeyProtected))
        {
            return string.Empty;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                Convert.FromBase64String(AiKeyProtected),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return string.Empty;
        }
    }

    [JsonIgnore]
    public bool HasAiKey => !string.IsNullOrEmpty(AiKeyProtected);
}
