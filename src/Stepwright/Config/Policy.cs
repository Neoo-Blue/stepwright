using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stepwright.Config;

/// <summary>
/// What an administrator has decided for this machine, and the person using it cannot undo.
///
/// A company that hands Stepwright to twenty technicians does not want twenty of them pasting
/// their own keys in, or one of them quietly pointing the assistant somewhere else. So a settings
/// file can be written once, by a script, into a place only an administrator can write to, and
/// everything named in it is fixed: the fields are filled in, greyed out, and left alone by the
/// app from then on.
///
/// A key set this way is never shown. Not masked with stars that can be copied out, not readable
/// in the settings file under the person's own profile: it is not written there at all, and the
/// page says who set it rather than what it is.
///
/// What this is honest about: the app has to be able to use the key, and the app runs as the
/// person. So this stops a technician changing what they should not change, and stops the key
/// being read out of a file or a screen. It is not armour against somebody with administrator
/// rights on their own machine and the will to pull the key out of a running program. Nothing
/// that keeps a usable key on a machine can be.
/// </summary>
public sealed class Policy
{
    /// <summary>Where a machine wide policy lives. Writable by administrators, readable by all.</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Stepwright",
        "policy.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The assistant, as the administrator set it.</summary>
    public string? AiProvider { get; set; }

    public string? AiAuth { get; set; }

    public string? AiBaseUrl { get; set; }

    public string? AiModel { get; set; }

    /// <summary>The key, encrypted for this machine. Never shown and never written anywhere else.</summary>
    public string? AiKeyProtected { get; set; }

    /// <summary>The Microsoft application and tenant, for the work account route.</summary>
    public string? AiAppId { get; set; }

    public string? AiTenant { get; set; }

    /// <summary>Publishing, as the administrator set it.</summary>
    public string? HuduBaseUrl { get; set; }

    public string? HuduKeyProtected { get; set; }

    public string? HuduPublish { get; set; }

    public string? ConfluenceSite { get; set; }

    public string? ConfluenceEmail { get; set; }

    public string? ConfluenceTokenProtected { get; set; }

    public string? ConfluenceAuth { get; set; }

    /// <summary>Where guides are kept, when a company wants them all in one place.</summary>
    public string? LibraryFolder { get; set; }

    /// <summary>The name of the company, shown on the settings page so people know who set this.</summary>
    public string? SetBy { get; set; }

    /// <summary>
    /// When true, everything named here is fixed and cannot be edited. When false, the values are
    /// filled in as a starting point and the person may change them, which suits a company that
    /// wants to save its technicians the setting up without taking the choice away.
    /// </summary>
    public bool Locked { get; set; } = true;

    // ------------------------------------------------------------------ reading it

    private static Policy? _loaded;
    private static bool _read;

    /// <summary>What this machine is under, read once. An absent or damaged file means no policy.</summary>
    public static Policy Current
    {
        get
        {
            if (_read)
            {
                return _loaded ?? Empty;
            }

            _read = true;

            try
            {
                if (File.Exists(Path))
                {
                    _loaded = JsonSerializer.Deserialize<Policy>(File.ReadAllText(Path), Options);
                }
            }
            catch
            {
                // A policy that cannot be read is no policy. The app still runs, because a
                // technician who cannot work is worse than one working with their own settings.
                _loaded = null;
            }

            return _loaded ?? Empty;
        }
    }

    private static readonly Policy Empty = new() { Locked = false };

    /// <summary>Reads the file again, for when a script has just written one.</summary>
    public static void Reload()
    {
        _read = false;
        _loaded = null;
    }

    [JsonIgnore]
    public bool Exists => !string.IsNullOrWhiteSpace(SetBy)
        || Has(AiProvider) || Has(AiKeyProtected) || Has(HuduBaseUrl) || Has(HuduKeyProtected)
        || Has(ConfluenceSite) || Has(ConfluenceTokenProtected) || Has(LibraryFolder)
        || Has(AiAppId) || Has(AiBaseUrl) || Has(AiModel);

    /// <summary>Who to name on the settings page when something is not the person's to change.</summary>
    [JsonIgnore]
    public string Who => string.IsNullOrWhiteSpace(SetBy) ? "your administrator" : SetBy!.Trim();

    private static bool Has(string? value) => !string.IsNullOrWhiteSpace(value);

    /// <summary>The sealed Confluence token, named as a method so the settings page reads plainly.</summary>
    public string? ConfluenceToken() => ConfluenceTokenProtected;

    /// <summary>True when this setting is fixed by policy and the person may not change it.</summary>
    public bool Fixed(string? value) => Locked && Has(value);

    // ------------------------------------------------------------------ the secrets

    /// <summary>
    /// Unlocks a secret the deployment script locked. It is encrypted to the machine rather than
    /// to a person, because the script that writes it and the technician who uses it are not the
    /// same account. That also means the file is worth nothing if it is copied to another machine.
    /// </summary>
    public static string Reveal(string? sealedValue)
    {
        if (string.IsNullOrWhiteSpace(sealedValue))
        {
            return string.Empty;
        }

        try
        {
            byte[] bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(sealedValue),
                optionalEntropy: Salt,
                DataProtectionScope.LocalMachine);

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // A secret sealed on another machine, or damaged in transit, is simply not there.
            return string.Empty;
        }
    }

    /// <summary>Seals a secret for this machine. Used by the tool that writes a policy.</summary>
    public static string Seal(string plain)
    {
        if (string.IsNullOrWhiteSpace(plain))
        {
            return string.Empty;
        }

        byte[] bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plain),
            optionalEntropy: Salt,
            DataProtectionScope.LocalMachine);

        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// A fixed extra ingredient, so a value sealed for Stepwright cannot be unsealed by simply
    /// asking Windows to unprotect it in some other program that never knew this was here.
    /// </summary>
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("Stepwright policy 1");
}
