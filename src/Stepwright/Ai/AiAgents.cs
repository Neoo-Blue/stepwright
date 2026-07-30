using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Stepwright.Config;

namespace Stepwright.Ai;

/// <summary>How the assistant proves who it is.</summary>
public static class AiAuthKinds
{
    /// <summary>A key bought from the service, billed by the token.</summary>
    public const string Key = "key";

    /// <summary>The command line app you already signed in to, paid for by your subscription.</summary>
    public const string Cli = "cli";

    /// <summary>A subscription token sent straight to the service. Advanced, and see the warning.</summary>
    public const string Token = "token";

    /// <summary>Signed in with a Microsoft work account, which is how Copilot and Foundry work.</summary>
    public const string Microsoft = "microsoft";

    public static string Clean(string? value) => value?.ToLowerInvariant() switch
    {
        Cli => Cli,
        Token => Token,
        Microsoft => Microsoft,
        _ => Key,
    };
}

/// <summary>
/// A command line app that is already signed in to a subscription. Stepwright runs it the way
/// a person would at a prompt, so the work is paid for by the plan the person already has and
/// no token ever passes through this app.
/// </summary>
public sealed class AiAgent
{
    /// <summary>Matches the service identifier in <see cref="AiProviders"/>.</summary>
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>The name of the command, without any extension.</summary>
    public required string Command { get; init; }

    /// <summary>What the person types once, in a terminal, to sign in.</summary>
    public required string SignIn { get; init; }

    /// <summary>The command a Sign in button runs for them, in a window they can see.</summary>
    public required string SignInCommand { get; init; }

    /// <summary>Which plan pays for it, said plainly for the settings page.</summary>
    public required string Plan { get; init; }

    public required string InstallPage { get; init; }

    /// <summary>Names worth offering in the model list, since there is no service to ask.</summary>
    public required IReadOnlyList<string> Models { get; init; }

    /// <summary>True when the app takes picture files as arguments rather than by being asked to read them.</summary>
    public bool AttachesPictures { get; init; }
}

public static class AiAgents
{
    public static readonly IReadOnlyList<AiAgent> All = new[]
    {
        new AiAgent
        {
            Id = AiProviders.Anthropic,
            Label = "Claude Code",
            Command = "claude",
            SignIn = "Run claude in a terminal once and sign in, or type /login inside it.",
            SignInCommand = "claude",
            Plan = "Claude Pro or Claude Max",
            InstallPage = "https://docs.claude.com/en/docs/claude-code/setup",
            Models = new[] { string.Empty, "sonnet", "opus", "haiku" },
        },
        new AiAgent
        {
            Id = AiProviders.OpenAi,
            Label = "Codex",
            Command = "codex",
            SignIn = "Run codex login in a terminal once and choose Sign in with ChatGPT.",
            SignInCommand = "codex login",
            Plan = "ChatGPT Plus, Pro, Business or Enterprise",
            InstallPage = "https://developers.openai.com/codex/cli",
            Models = new[] { string.Empty, "gpt-5.1-codex", "gpt-5.1" },
            AttachesPictures = true,
        },
        new AiAgent
        {
            Id = AiProviders.Gemini,
            Label = "Gemini CLI",
            Command = "gemini",
            SignIn = "Run gemini in a terminal once and sign in with your Google account.",
            SignInCommand = "gemini",
            Plan = "A Google account, or a Gemini Code Assist plan",
            InstallPage = "https://github.com/google-gemini/gemini-cli",
            Models = new[] { string.Empty, "gemini-2.5-pro", "gemini-2.5-flash" },
        },
    };

    public static AiAgent? Find(string? providerId) =>
        All.FirstOrDefault(a => string.Equals(a.Id, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the chosen service has an app that can stand in for a key.</summary>
    public static bool Supports(string? providerId) => Find(providerId) is not null;

    // ------------------------------------------------------------------ finding the app

    /// <summary>
    /// Where the app is on this machine, or null when it is not installed. The path saved in
    /// settings wins, then the search path, then the places these apps normally install to.
    /// </summary>
    public static string? Locate(AiAgent agent, string? saved = null)
    {
        if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
        {
            return saved;
        }

        foreach (string candidate in Candidates(agent.Command))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Candidates(string command)
    {
        string[] extensions = OperatingSystem.IsWindows()
            ? new[] { ".cmd", ".exe", ".bat", ".ps1" }
            : new[] { string.Empty };

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string extension in extensions)
            {
                yield return SafeCombine(folder.Trim('"'), command + extension);
            }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] folders = OperatingSystem.IsWindows()
            ? new[]
            {
                SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                SafeCombine(home, ".local", "bin"),
                SafeCombine(home, ".bun", "bin"),
                SafeCombine(home, "AppData", "Local", "Programs", command),
                SafeCombine(home, "scoop", "shims"),
                @"C:\Program Files\nodejs",
            }
            : new[]
            {
                "/opt/homebrew/bin",
                "/usr/local/bin",
                SafeCombine(home, ".local", "bin"),
                SafeCombine(home, ".bun", "bin"),
                "/usr/bin",
            };

        foreach (string folder in folders)
        {
            foreach (string extension in extensions)
            {
                yield return SafeCombine(folder, command + extension);
            }
        }
    }

    private static string SafeCombine(params string[] parts)
    {
        try
        {
            return Path.Combine(parts);
        }
        catch (ArgumentException)
        {
            // A search path can hold characters no path may contain, and that is not fatal.
            return string.Empty;
        }
    }

    /// <summary>Asks the app for its version, which proves it can actually run.</summary>
    public static async Task<string> VersionAsync(AiAgent agent, string? saved, CancellationToken token)
    {
        string path = Locate(agent, saved)
            ?? throw new InvalidOperationException(
                $"{agent.Label} is not on this machine. Install it, then sign in with your {agent.Plan} account.");

        (int code, string output, string error) = await RunAsync(
            path,
            new[] { "--version" },
            input: null,
            workingFolder: Path.GetTempPath(),
            TimeSpan.FromSeconds(30),
            token).ConfigureAwait(false);

        if (code != 0)
        {
            throw new InvalidOperationException($"{agent.Label} could not be started. {Trim(error, output)}");
        }

        string version = First(output);
        return string.IsNullOrWhiteSpace(version) ? agent.Label + " answered." : version;
    }

    // ------------------------------------------------------------------ asking a question

    /// <summary>
    /// Puts one question to the signed in app and gives back what it said. The whole prompt goes
    /// in through the keyboard side of the pipe, so nothing has to survive being quoted on a
    /// command line, and everything is written inside a folder that is deleted afterwards.
    /// </summary>
    public static async Task<string> CompleteAsync(
        AppSettings settings,
        string system,
        string user,
        IReadOnlyList<byte[]>? pictures,
        CancellationToken token)
    {
        AiAgent agent = Find(settings.AiProvider)
            ?? throw new InvalidOperationException(
                "That service has no app to sign in with. Choose a different service, or use a key.");

        string path = Locate(agent, settings.AiCliPath)
            ?? throw new InvalidOperationException(
                $"{agent.Label} is not on this machine. Install it, then sign in with your {agent.Plan} account.");

        string folder = Path.Combine(Path.GetTempPath(), "stepwright-ai-" + Guid.NewGuid().ToString("n")[..12]);
        Directory.CreateDirectory(folder);

        try
        {
            var files = new List<string>();
            int number = 0;

            foreach (byte[] picture in pictures ?? Array.Empty<byte[]>())
            {
                number++;
                string name = $"screenshot{number}.jpg";
                await File.WriteAllBytesAsync(Path.Combine(folder, name), picture, token).ConfigureAwait(false);
                files.Add(name);
            }

            string prompt = Prompt(agent, system, user, files);
            string[] arguments = Arguments(agent, settings.AiModel, folder, files);

            (int code, string output, string error) = await RunAsync(
                path,
                arguments,
                prompt,
                folder,
                TimeSpan.FromMinutes(10),
                token).ConfigureAwait(false);

            if (code != 0)
            {
                throw new InvalidOperationException(
                    $"{agent.Label} stopped with an error. {Trim(error, output)}");
            }

            string answer = Answer(agent, folder, output);

            return string.IsNullOrWhiteSpace(answer)
                ? throw new InvalidOperationException($"{agent.Label} finished but said nothing. {Trim(error, output)}")
                : answer;
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
                // A file still held open is no reason to fail a run that worked.
            }
        }
    }

    private static string Prompt(AiAgent agent, string system, string user, IReadOnlyList<string> pictures)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine(system);
        prompt.AppendLine();

        if (pictures.Count > 0 && !agent.AttachesPictures)
        {
            // Nothing is attached for these apps, so the picture is named and they open it themselves.
            string names = agent.Id == AiProviders.Gemini
                ? string.Join(" ", pictures.Select(p => "@" + p))
                : string.Join(", ", pictures);

            prompt.AppendLine($"Look at {names} in this folder before you answer.");
            prompt.AppendLine();
        }

        prompt.Append(user);
        return prompt.ToString();
    }

    private static string[] Arguments(AiAgent agent, string? model, string folder, IReadOnlyList<string> pictures)
    {
        var arguments = new List<string>();
        string chosen = (model ?? string.Empty).Trim();

        switch (agent.Id)
        {
            case AiProviders.Anthropic:
                arguments.Add("--print");
                arguments.Add("--output-format");
                arguments.Add("json");

                if (chosen.Length > 0)
                {
                    arguments.Add("--model");
                    arguments.Add(chosen);
                }

                if (pictures.Count > 0)
                {
                    // Reading the screenshot is the only thing it is allowed to do.
                    arguments.Add("--allowed-tools");
                    arguments.Add("Read");
                }

                break;

            case AiProviders.OpenAi:
                arguments.Add("exec");
                arguments.Add("--skip-git-repo-check");
                arguments.Add("--sandbox");
                arguments.Add("read-only");
                arguments.Add("--color");
                arguments.Add("never");
                arguments.Add("--output-last-message");
                arguments.Add(Path.Combine(folder, AnswerFile));

                if (chosen.Length > 0)
                {
                    arguments.Add("--model");
                    arguments.Add(chosen);
                }

                foreach (string picture in pictures)
                {
                    arguments.Add("--image");
                    arguments.Add(Path.Combine(folder, picture));
                }

                // A single dash tells it the question is arriving through the pipe.
                arguments.Add("-");
                break;

            default:
                arguments.Add("--output-format");
                arguments.Add("json");

                if (chosen.Length > 0)
                {
                    arguments.Add("--model");
                    arguments.Add(chosen);
                }

                break;
        }

        return arguments.ToArray();
    }

    private const string AnswerFile = "answer.txt";

    /// <summary>
    /// Each app wraps the answer differently. The file Codex writes is exact, the other two
    /// wrap it in a small envelope, and anything unexpected falls back to the raw output.
    /// </summary>
    private static string Answer(AiAgent agent, string folder, string output)
    {
        if (agent.Id == AiProviders.OpenAi)
        {
            string file = Path.Combine(folder, AnswerFile);

            if (File.Exists(file))
            {
                string written = File.ReadAllText(file).Trim();
                if (written.Length > 0)
                {
                    return written;
                }
            }

            return output.Trim();
        }

        try
        {
            if (JsonNode.Parse(output) is JsonNode node)
            {
                string? text = node["result"]?.GetValue<string>() ?? node["response"]?.GetValue<string>();

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }

                // An envelope that carries an error rather than an answer.
                string? failed = node["error"]?["message"]?.GetValue<string>() ?? node["error"]?.ToString();
                if (!string.IsNullOrWhiteSpace(failed))
                {
                    throw new InvalidOperationException($"{agent.Label} said: {failed}");
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not an envelope, so the text it printed is the answer.
        }

        return output.Trim();
    }

    // ------------------------------------------------------------------ running the process

    private static async Task<(int Code, string Output, string Error)> RunAsync(
        string path,
        IReadOnlyList<string> arguments,
        string? input,
        string workingFolder,
        TimeSpan limit,
        CancellationToken token)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workingFolder,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Windows installs these apps as script shims, and a shim can only be run through the
        // command processor. Everywhere else the file itself is the program.
        bool shim = OperatingSystem.IsWindows()
            && (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase));

        if (shim)
        {
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";

            // The whole command is quoted a second time on purpose. Without that, and without
            // /s, the command processor strips the quotes around the path and an install under
            // a folder with a space in its name stops working.
            start.Arguments = "/d /s /c \"" + Quote(path)
                + string.Concat(arguments.Select(a => " " + Quote(a))) + "\"";
        }
        else
        {
            start.FileName = path;

            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        // A key left in the environment would quietly move the work onto paid billing, which is
        // the one thing this route exists to avoid.
        foreach (string name in new[]
                 {
                     "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_BASE_URL",
                     "OPENAI_API_KEY", "OPENAI_BASE_URL",
                     "GEMINI_API_KEY", "GOOGLE_API_KEY", "GOOGLE_GENAI_USE_VERTEXAI",
                 })
        {
            start.Environment.Remove(name);
        }

        using var process = new Process { StartInfo = start };

        var output = new StringBuilder();
        var error = new StringBuilder();
        using var finished = new SemaphoreSlim(0, 2);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                finished.Release();
            }
            else
            {
                output.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                finished.Release();
            }
            else
            {
                error.AppendLine(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (Exception failure)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} could not be started. {failure.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(limit);

        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), stop.Token).ConfigureAwait(false);
            }

            process.StandardInput.Close();

            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);

            // Both readers close before the collected text can be trusted to be complete.
            await finished.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            await finished.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            if (token.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException(
                $"{Path.GetFileName(path)} was still running after {limit.TotalMinutes:0} minutes and was stopped.");
        }
        catch (IOException)
        {
            // The app closed the pipe early, which is normal when it rejects the request.
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
        }

        return (process.ExitCode, output.ToString(), error.ToString());
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // It may have exited between the check and the request, which is the outcome wanted.
        }
    }

    /// <summary>Wraps a value so the command processor reads it as one thing.</summary>
    private static string Quote(string value) =>
        value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"', '&', '(', ')', '^' }) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string First(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;

    /// <summary>The most useful line to show a person when something went wrong.</summary>
    private static string Trim(string error, string output)
    {
        string text = string.IsNullOrWhiteSpace(error) ? output : error;
        text = text.Replace("\r", string.Empty, StringComparison.Ordinal).Trim();

        if (text.Length == 0)
        {
            return "It gave no reason.";
        }

        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
