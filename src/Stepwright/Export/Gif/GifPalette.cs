namespace Stepwright.Export.Gif;

/// <summary>
/// Reduces the colours of a picture to the 256 a gif can hold, by repeatedly splitting the
/// cloud of colours along its widest axis and taking the average of each part. Screenshots
/// are mostly flat interface colours, so the result is usually indistinguishable.
///
/// Nothing here touches the platform, so it can be exercised anywhere.
/// </summary>
public sealed class GifPalette
{
    private readonly byte[] _table = new byte[768];
    private readonly Dictionary<int, byte> _cache = new();
    private int _count;

    private GifPalette()
    {
    }

    /// <summary>The colour table as a gif wants it: red, green and blue for each of 256 slots.</summary>
    public byte[] Table => _table;

    public int Count => _count;

    /// <summary>Builds a palette from pixels in the usual packed form, alpha in the top byte.</summary>
    public static GifPalette Build(IReadOnlyList<int> pixels, int maxColors = 256)
    {
        var palette = new GifPalette();

        if (pixels.Count == 0)
        {
            palette._count = 1;
            return palette;
        }

        // Sampling keeps this quick on a large screen without changing the outcome much.
        int step = Math.Max(1, pixels.Count / 60000);
        var sample = new List<int>(Math.Min(pixels.Count, 60000));
        for (int i = 0; i < pixels.Count; i += step)
        {
            sample.Add(pixels[i]);
        }

        var boxes = new List<List<int>> { sample };

        while (boxes.Count < maxColors)
        {
            int widest = -1;
            int widestSpread = 0;
            int widestChannel = 0;

            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Count < 2)
                {
                    continue;
                }

                (int spread, int channel) = Spread(boxes[i]);
                if (spread > widestSpread)
                {
                    widestSpread = spread;
                    widest = i;
                    widestChannel = channel;
                }
            }

            if (widest < 0 || widestSpread == 0)
            {
                break;
            }

            List<int> box = boxes[widest];
            box.Sort((a, b) => Channel(a, widestChannel).CompareTo(Channel(b, widestChannel)));

            int middle = box.Count / 2;
            var left = box.GetRange(0, middle);
            var right = box.GetRange(middle, box.Count - middle);

            boxes[widest] = left;
            boxes.Add(right);
        }

        palette._count = Math.Max(1, boxes.Count);

        for (int i = 0; i < boxes.Count && i < maxColors; i++)
        {
            (byte r, byte g, byte b) = Average(boxes[i]);
            palette._table[(i * 3) + 0] = r;
            palette._table[(i * 3) + 1] = g;
            palette._table[(i * 3) + 2] = b;
        }

        return palette;
    }

    /// <summary>Finds the closest slot for a colour, remembering answers as it goes.</summary>
    public byte IndexOf(int pixel)
    {
        int key = pixel & 0x00FFFFFF;
        if (_cache.TryGetValue(key, out byte cached))
        {
            return cached;
        }

        int r = (pixel >> 16) & 0xFF;
        int g = (pixel >> 8) & 0xFF;
        int b = pixel & 0xFF;

        int best = 0;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < _count; i++)
        {
            int dr = r - _table[(i * 3) + 0];
            int dg = g - _table[(i * 3) + 1];
            int db = b - _table[(i * 3) + 2];

            // Weighted towards green, which the eye is most sensitive to.
            int distance = (dr * dr * 3) + (dg * dg * 6) + (db * db * 1);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        var index = (byte)best;

        // The cache is bounded, because a photograph could otherwise fill it with noise.
        if (_cache.Count < 200000)
        {
            _cache[key] = index;
        }

        return index;
    }

    public byte[] Map(IReadOnlyList<int> pixels)
    {
        var indices = new byte[pixels.Count];
        for (int i = 0; i < pixels.Count; i++)
        {
            indices[i] = IndexOf(pixels[i]);
        }

        return indices;
    }

    private static (int Spread, int Channel) Spread(List<int> box)
    {
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;

        foreach (int pixel in box)
        {
            int r = (pixel >> 16) & 0xFF;
            int g = (pixel >> 8) & 0xFF;
            int b = pixel & 0xFF;

            minR = Math.Min(minR, r);
            maxR = Math.Max(maxR, r);
            minG = Math.Min(minG, g);
            maxG = Math.Max(maxG, g);
            minB = Math.Min(minB, b);
            maxB = Math.Max(maxB, b);
        }

        int spreadR = maxR - minR;
        int spreadG = maxG - minG;
        int spreadB = maxB - minB;

        if (spreadG >= spreadR && spreadG >= spreadB)
        {
            return (spreadG, 1);
        }

        return spreadR >= spreadB ? (spreadR, 0) : (spreadB, 2);
    }

    private static int Channel(int pixel, int channel) => channel switch
    {
        0 => (pixel >> 16) & 0xFF,
        1 => (pixel >> 8) & 0xFF,
        _ => pixel & 0xFF,
    };

    private static (byte R, byte G, byte B) Average(List<int> box)
    {
        if (box.Count == 0)
        {
            return (0, 0, 0);
        }

        long r = 0, g = 0, b = 0;
        foreach (int pixel in box)
        {
            r += (pixel >> 16) & 0xFF;
            g += (pixel >> 8) & 0xFF;
            b += pixel & 0xFF;
        }

        return ((byte)(r / box.Count), (byte)(g / box.Count), (byte)(b / box.Count));
    }
}
