using System.Reflection;

namespace Stepwright;

/// <summary>
/// Which build this is.
///
/// This exists for one plain reason: when a person sends a picture of something going wrong, the
/// first question is always which version they are looking at, and a build that cannot answer
/// that turns every such picture into a guess. So the version is stamped in at build time and
/// shown on the windows, where a picture of the window carries it along.
/// </summary>
public static class Build
{
    /// <summary>The version, as "1.16.2", or an empty string on a build that was never stamped.</summary>
    public static string Version
    {
        get
        {
            // The informational version carries whatever was passed at build time, including a
            // suffix if there was one. The plain assembly version is the fallback.
            string informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? string.Empty;

            // The compiler appends a build metadata hash after a plus sign; nobody needs that.
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            if (plus >= 0)
            {
                informational = informational[..plus];
            }

            if (informational.Length > 0 && informational != "1.0.0")
            {
                return informational;
            }

            Version? assembly = Assembly.GetExecutingAssembly().GetName().Version;

            return assembly is null || (assembly.Major == 1 && assembly.Minor == 0 && assembly.Build == 0)
                ? string.Empty
                : $"{assembly.Major}.{assembly.Minor}.{assembly.Build}";
        }
    }

    /// <summary>The app name with the version after it, for a window title.</summary>
    public static string Titled(string name)
    {
        string version = Version;
        return version.Length > 0 ? $"{name}  {version}" : name;
    }
}
