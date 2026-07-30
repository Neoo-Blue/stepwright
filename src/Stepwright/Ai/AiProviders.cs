namespace Stepwright.Ai;

/// <summary>A service the assistant can talk to, and sensible starting values for it.</summary>
public sealed class AiProvider
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required string BaseUrl { get; init; }

    public required string Model { get; init; }

    /// <summary>Where the person goes to create a key.</summary>
    public required string KeyPage { get; init; }

    public required string Hint { get; init; }

    public bool SupportsPictures { get; init; } = true;
}

public static class AiProviders
{
    public const string OpenAi = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string Copilot = "copilot";
    public const string Foundry = "foundry";
    public const string Custom = "custom";

    public static readonly IReadOnlyList<AiProvider> All = new[]
    {
        new AiProvider
        {
            Id = OpenAi,
            Label = "OpenAI, the GPT models",
            BaseUrl = "https://api.openai.com/v1",
            Model = "gpt-4o-mini",
            KeyPage = "https://platform.openai.com/api-keys",
            Hint = "Key starts with sk. Any model that can read pictures works, for example gpt-4o.",
        },
        new AiProvider
        {
            Id = Anthropic,
            Label = "Anthropic, the Claude models",
            BaseUrl = "https://api.anthropic.com",
            Model = "claude-sonnet-5",
            KeyPage = "https://console.anthropic.com/settings/keys",
            Hint = "Key starts with sk ant. Every Claude model can read pictures.",
        },
        new AiProvider
        {
            Id = Gemini,
            Label = "Google, the Gemini models",
            BaseUrl = "https://generativelanguage.googleapis.com",
            Model = "gemini-2.0-flash",
            KeyPage = "https://aistudio.google.com/apikey",
            Hint = "Key from AI Studio. Every Gemini model can read pictures.",
        },
        new AiProvider
        {
            Id = Copilot,
            Label = "Microsoft 365 Copilot",
            BaseUrl = "https://graph.microsoft.com/beta",
            Model = string.Empty,
            KeyPage = MicrosoftOAuth.PortalPage,
            Hint = "Signs in as you and uses the Copilot licence you already pay for. Text only.",
            SupportsPictures = false,
        },
        new AiProvider
        {
            Id = Foundry,
            Label = "Azure AI Foundry",
            BaseUrl = "https://your-resource.openai.azure.com",
            Model = "gpt-4o",
            KeyPage = "https://ai.azure.com",
            Hint = "Your own deployment. The model box is the name you gave the deployment.",
        },
        new AiProvider
        {
            Id = Custom,
            Label = "Anything that speaks the OpenAI shape",
            BaseUrl = "http://localhost:11434/v1",
            Model = "llama3.2",
            KeyPage = string.Empty,
            Hint = "Your own gateway, a local model, or any service with a chat completions endpoint.",
        },
    };

    public static AiProvider Find(string? id) =>
        All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}
