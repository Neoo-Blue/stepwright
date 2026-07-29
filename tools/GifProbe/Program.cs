using Stepwright.Export.Gif;

namespace GifProbe;

/// <summary>
/// Produces an animation from pictures made in memory, so the writer can be proved correct
/// on any machine. The pattern is deliberately awkward: sharp edges, a gradient, and a moving
/// shape, which together catch both a bad palette and a bad compressor.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string output = args.Length > 0 ? args[0] : "probe.gif";
        int width = 320;
        int height = 200;
        int frameCount = 12;

        var frames = new List<int[]>();

        for (int f = 0; f < frameCount; f++)
        {
            var pixels = new int[width * height];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // A gradient background with a hard edged band, so banding and edge
                    // errors both show up.
                    int r = x * 255 / width;
                    int g = y * 255 / height;
                    int b = ((x / 24) + (y / 24)) % 2 == 0 ? 40 : 200;

                    // A circle that moves across the picture from frame to frame.
                    int cx = 40 + (f * (width - 80) / Math.Max(1, frameCount - 1));
                    int cy = height / 2;
                    int dx = x - cx;
                    int dy = y - cy;

                    if ((dx * dx) + (dy * dy) < 26 * 26)
                    {
                        r = 230;
                        g = 60;
                        b = 60;
                    }

                    pixels[(y * width) + x] = (255 << 24) | (r << 16) | (g << 8) | b;
                }
            }

            frames.Add(pixels);
        }

        // One palette for the whole animation, built from every picture in it.
        var all = new List<int>();
        foreach (int[] frame in frames)
        {
            for (int i = 0; i < frame.Length; i += 3)
            {
                all.Add(frame[i]);
            }
        }

        GifPalette palette = GifPalette.Build(all);
        Console.WriteLine($"palette holds {palette.Count} colours");

        var encoded = new List<GifFrame>();
        for (int i = 0; i < frames.Count; i++)
        {
            encoded.Add(new GifFrame
            {
                Indices = palette.Map(frames[i]),
                DelayCentiseconds = i == frames.Count - 1 ? 120 : 8,
            });
        }

        using (var stream = new FileStream(output, FileMode.Create, FileAccess.Write))
        {
            GifWriter.Write(stream, width, height, palette, encoded);
        }

        var info = new FileInfo(output);
        Console.WriteLine($"wrote {output}, {info.Length} bytes, {encoded.Count} pictures");
        return 0;
    }
}
