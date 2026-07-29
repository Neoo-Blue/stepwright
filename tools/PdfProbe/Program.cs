using Stepwright.Export.Pdf;

namespace PdfProbe;

/// <summary>
/// Drives the same layout the app ships, with made up content chosen to stress it: text that
/// has to wrap, characters well outside the Latin range, notes, headings, page breaks and
/// both jpeg colour spaces. The output is meant to be opened by a real reader.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "probe.pdf";
        byte[]? regular = Read(args.Length > 1 ? args[1] : string.Empty);
        byte[]? bold = Read(args.Length > 2 ? args[2] : string.Empty);
        byte[]? colour = Read(args.Length > 3 ? args[3] : string.Empty);
        byte[]? grey = Read(args.Length > 4 ? args[4] : string.Empty);

        Console.WriteLine($"regular font: {regular is not null}, bold font: {bold is not null}, "
            + $"colour picture: {colour is not null}, grey picture: {grey is not null}");

        string[] lines =
        {
            "Click “Sign in” button in Google Chrome.",
            "Type “billing@cooli.ai” in the “Email address” field.",
            "Enter your password.",
            "Press Ctrl + S.",
            "Scroll down in the customer list until the account appears, then keep going until the "
                + "row you want is visible near the middle of the window where it is easiest to read.",
            "Türkçe karakterler: şu ğamı İstanbul çok güzel.",
            "Symbols and quotes: £ € ½ … “quoted” ‘single’ ± × ÷.",
            "Averyverylongwordwithnospacesatallthatcannotpossiblyfitinsideonesinglecolumnofthispage.",
        };

        var items = new List<PdfGuideItem>();
        int number = 0;

        for (int i = 0; i < 14; i++)
        {
            if (i == 0 || i == 6)
            {
                items.Add(new PdfGuideItem
                {
                    IsHeading = true,
                    Text = i == 0 ? "Signing in" : "Finding the account",
                });
            }

            number++;
            items.Add(new PdfGuideItem
            {
                Number = number,
                Text = lines[i % lines.Length],
                Note = i % 4 == 3 ? "The account stays locked for ten minutes after the third attempt." : null,
                Picture = i % 3 == 2 ? grey : colour,
            });
        }

        var header = new PdfGuideHeader
        {
            Title = "Resetting a password in the billing portal",
            Summary = "How to reset a customer password without removing their saved payment details.",
            Author = "Arafat Erkin",
            DateLine = "28 July 2026",
            StepCount = number,
        };

        byte[] pdf = PdfGuideLayout.Build(header, items, regular, bold, "ProbeFace");
        File.WriteAllBytes(output, pdf);

        Console.WriteLine($"wrote {output}, {pdf.Length} bytes");
        return 0;
    }

    private static byte[]? Read(string path) =>
        !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllBytes(path) : null;
}
