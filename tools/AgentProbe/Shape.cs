using Stepwright.Ai;
using Stepwright.Config;
using Stepwright.Model;

namespace AgentProbe;

/// <summary>
/// Runs the shaping pass over a guide built here, so the whole path can be proved: the rules
/// that go to the assistant, the plan it sends back, and what the plan does to the steps.
/// </summary>
internal static class Shape
{
    public static async Task<int> RunAsync(string provider, CancellationToken token)
    {
        var guide = new Guide
        {
            Title = "Adding a deletion notice to a Confluence page",
            Summary = "open a machine's Confluence documentation page and record the date the machine was decommissioned.",
        };

        void Add(StepKind kind, string text, string typed = "", string keys = "", string window = "")
        {
            guide.Steps.Add(new Step
            {
                Kind = kind,
                Text = text,
                TypedText = typed,
                Keys = keys,
                AppName = "Google Chrome",
                WindowTitle = window,
                Image = "shot.png",
            });
        }

        Add(StepKind.Click, "Click the “New tab” button in the browser tab bar.");
        Add(StepKind.Type, "Type “12” in the “Search Google or type a URL” address bar.", typed: "12");
        Add(StepKind.Hotkey, "Type “12” in the address bar and press “Enter”.", typed: "12", keys: "Enter");
        Add(StepKind.Click, "Click the address bar at the top of the browser window.");
        Add(StepKind.Type, "Type “conf” in the “Address and search bar”.", typed: "conf");
        Add(StepKind.Hotkey, "Press “Enter” to open the suggested “For you  Confluence”.", keys: "Enter");
        Add(StepKind.Click, "Click the “Search” field at the top of the page.", window: "For you  Confluence");
        Add(StepKind.Click, "Click the first result under “RECOMMENDATIONS”.", window: "For you  Confluence");

        var settings = new AppSettings
        {
            AiProvider = provider,
            AiAuth = AiAuthKinds.Cli,
            AiModel = string.Empty,
        };

        Console.WriteLine($"before: {guide.Steps.Count} steps");

        var progress = new Progress<string>(Console.WriteLine);
        ShapeResult result = await AiShaper.ShapeAsync(guide, settings, progress, token).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(result.Describe());
        Console.WriteLine();

        int number = 0;
        foreach (Step step in guide.Steps)
        {
            number++;
            Console.WriteLine(step.Skip
                ? $"  {number}. [set aside] {step.Text}"
                : $"  {number}. {step.Text}");
        }

        int visible = guide.Steps.Count(s => !s.Skip);
        Console.WriteLine();
        Console.WriteLine($"after: {visible} steps a reader follows");

        return visible < 8 && visible > 0 ? 0 : 1;
    }
}
