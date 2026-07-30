using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Stepwright.Ai;
using Stepwright.Config;

namespace AgentProbe;

/// <summary>
/// Puts one question to a signed in command line app the way Stepwright does, and prints what
/// came back. It proves the arguments, the pipe and the shape of the answer on any machine
/// where one of those apps is installed.
///
/// Usage: AgentProbe [anthropic|openai|gemini] [path to a picture]
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string provider = args.Length > 0 ? args[0] : AiProviders.Anthropic;
        string picture = args.Length > 1 ? args[1] : string.Empty;

        AiAgent? agent = AiAgents.Find(provider);

        if (agent is null)
        {
            Console.Error.WriteLine("No app stands in for " + provider);
            return 2;
        }

        string? where = AiAgents.Locate(agent);
        Console.WriteLine($"{agent.Label}: {where ?? "not installed"}");

        if (where is null)
        {
            return 3;
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        Console.WriteLine("version: " + await AiAgents.VersionAsync(agent, null, cancel.Token).ConfigureAwait(false));

        var settings = new AppSettings
        {
            AiProvider = agent.Id,
            AiAuth = AiAuthKinds.Cli,
            AiModel = string.Empty,
        };

        var pictures = new List<byte[]>();

        if (picture.Length > 0)
        {
            pictures.Add(await File.ReadAllBytesAsync(picture, cancel.Token).ConfigureAwait(false));
        }

        string system =
            "You write instructions for a step by step guide. "
            + (pictures.Count > 0
                ? "Say what is in the picture you are shown. "
                : string.Empty)
            + "Reply with JSON only in the form {\"text\":\"...\",\"note\":\"...\"}.";

        string user = pictures.Count > 0
            ? "Describe what this screenshot shows in one short sentence."
            : "Rewrite this step: user clicked thing labelled Save.";

        DateTime started = DateTime.UtcNow;

        string reply = await AiAgents
            .CompleteAsync(settings, system, user, pictures, cancel.Token)
            .ConfigureAwait(false);

        Console.WriteLine($"took {(DateTime.UtcNow - started).TotalSeconds:0.0} seconds");
        Console.WriteLine("reply:");
        Console.WriteLine(reply);

        return reply.Contains('{') ? 0 : 1;
    }
}
