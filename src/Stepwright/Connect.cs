namespace Stepwright;

/// <summary>
/// Where the published applications live.
///
/// Every service in this app can be connected two ways: the company registers its own
/// application and pastes identifiers into Settings, or the person who built this copy has
/// already registered one and nobody else has to think about it. These are the values for the
/// second way, and they are the difference between a technician pressing Sign in and a
/// technician booking a meeting with an administrator.
///
/// They are deliberately empty in the public source. Fill them in your own build. None of them
/// is a secret: an application identifier is a name, and the broker address is a public
/// endpoint that holds the one real secret on your side rather than on the machine.
/// </summary>
public static class Connect
{
    /// <summary>
    /// A multi tenant application registered in your Microsoft tenant, marked as a public
    /// client. Customers then never register anything: their administrator approves yours once
    /// and every technician in that company simply signs in.
    /// </summary>
    public const string MicrosoftAppId = "";

    /// <summary>
    /// The address of the sign in broker under broker/ in this repository. Atlassian refuses to
    /// exchange a code without a client secret, so the secret lives on that worker rather than
    /// in this file, where it could be read by anybody holding the executable.
    /// </summary>
    public const string AtlassianBroker = "";

    public static bool HasMicrosoft => MicrosoftAppId.Trim().Length > 0;

    public static bool HasBroker => AtlassianBroker.Trim().Length > 0;

    /// <summary>The broker address without a trailing slash, ready to have a path added.</summary>
    public static string Broker => AtlassianBroker.Trim().TrimEnd('/');
}
