namespace Stepwright.Export.Pdf;

/// <summary>
/// Just enough of a TrueType file to embed it in a document: the character to glyph map,
/// the advance widths and the few numbers a font descriptor needs.
///
/// Nothing here touches the platform, so the whole file can be exercised on any machine.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ushort> _glyphCache = new();
    private readonly Dictionary<ushort, int> _advanceCache = new();

    private int _cmapOffset;
    private int _cmapFormat;
    private int _hmtxOffset;
    private int _numberOfHMetrics;

    private TrueTypeFont(byte[] data, string name)
    {
        _data = data;
        Name = name;
    }

    public string Name { get; }

    public byte[] Data => _data;

    public int UnitsPerEm { get; private set; } = 1000;

    public short Ascender { get; private set; } = 800;

    public short Descender { get; private set; } = -200;

    public short XMin { get; private set; }

    public short YMin { get; private set; }

    public short XMax { get; private set; } = 1000;

    public short YMax { get; private set; } = 1000;

    /// <summary>Reads a font file. Returns null when the bytes are not a usable TrueType face.</summary>
    public static TrueTypeFont? Load(byte[] data, string name)
    {
        try
        {
            var font = new TrueTypeFont(data, Sanitize(name));
            return font.Parse() ? font : null;
        }
        catch
        {
            return null;
        }
    }

    private bool Parse()
    {
        if (_data.Length < 12)
        {
            return false;
        }

        uint version = ReadUInt32(0);

        // A font collection needs a different entry point, and an OpenType face with
        // curves in a compact table has no glyf table to embed this way.
        if (version != 0x00010000 && version != 0x74727565)
        {
            return false;
        }

        int tableCount = ReadUInt16(4);
        if (tableCount <= 0 || tableCount > 512)
        {
            return false;
        }

        for (int i = 0; i < tableCount; i++)
        {
            int record = 12 + (i * 16);
            if (record + 16 > _data.Length)
            {
                return false;
            }

            string tag = System.Text.Encoding.ASCII.GetString(_data, record, 4);
            int offset = (int)ReadUInt32(record + 8);
            int length = (int)ReadUInt32(record + 12);

            if (offset >= 0 && length >= 0 && offset + length <= _data.Length)
            {
                _tables[tag] = (offset, length);
            }
        }

        if (!_tables.TryGetValue("head", out var head)
            || !_tables.TryGetValue("hhea", out var hhea)
            || !_tables.TryGetValue("hmtx", out var hmtx)
            || !_tables.TryGetValue("cmap", out var cmap)
            || !_tables.ContainsKey("glyf"))
        {
            return false;
        }

        UnitsPerEm = ReadUInt16(head.Offset + 18);
        if (UnitsPerEm <= 0)
        {
            UnitsPerEm = 1000;
        }

        XMin = ReadInt16(head.Offset + 36);
        YMin = ReadInt16(head.Offset + 38);
        XMax = ReadInt16(head.Offset + 40);
        YMax = ReadInt16(head.Offset + 42);

        Ascender = ReadInt16(hhea.Offset + 4);
        Descender = ReadInt16(hhea.Offset + 6);
        _numberOfHMetrics = ReadUInt16(hhea.Offset + 34);
        _hmtxOffset = hmtx.Offset;

        return SelectCharacterMap(cmap.Offset);
    }

    private bool SelectCharacterMap(int tableOffset)
    {
        int count = ReadUInt16(tableOffset + 2);
        int best = -1;
        int bestScore = -1;

        for (int i = 0; i < count; i++)
        {
            int record = tableOffset + 4 + (i * 8);
            if (record + 8 > _data.Length)
            {
                break;
            }

            int platform = ReadUInt16(record);
            int encoding = ReadUInt16(record + 2);
            int offset = tableOffset + (int)ReadUInt32(record + 4);

            if (offset + 4 > _data.Length)
            {
                continue;
            }

            int format = ReadUInt16(offset);

            // Prefer the full range map, then the common Windows Unicode map.
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) => 5,
                (0, _, 12) => 4,
                (3, 1, 4) => 3,
                (0, _, 4) => 2,
                (_, _, 4) => 1,
                _ => -1,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = offset;
                _cmapFormat = format;
            }
        }

        if (best < 0)
        {
            return false;
        }

        _cmapOffset = best;
        return true;
    }

    /// <summary>Glyph for a unicode code point, or zero when the font has no such glyph.</summary>
    public ushort GlyphFor(int codePoint)
    {
        if (_glyphCache.TryGetValue(codePoint, out ushort cached))
        {
            return cached;
        }

        ushort glyph = _cmapFormat == 12 ? LookupFormat12(codePoint) : LookupFormat4(codePoint);
        _glyphCache[codePoint] = glyph;
        return glyph;
    }

    private ushort LookupFormat4(int codePoint)
    {
        if (codePoint > 0xFFFF)
        {
            return 0;
        }

        int segCountX2 = ReadUInt16(_cmapOffset + 6);
        int segCount = segCountX2 / 2;
        int endCodes = _cmapOffset + 14;
        int startCodes = endCodes + segCountX2 + 2;
        int deltas = startCodes + segCountX2;
        int rangeOffsets = deltas + segCountX2;

        for (int segment = 0; segment < segCount; segment++)
        {
            int end = ReadUInt16(endCodes + (segment * 2));
            if (codePoint > end)
            {
                continue;
            }

            int start = ReadUInt16(startCodes + (segment * 2));
            if (codePoint < start)
            {
                return 0;
            }

            short delta = ReadInt16(deltas + (segment * 2));
            int rangeOffsetPosition = rangeOffsets + (segment * 2);
            int rangeOffset = ReadUInt16(rangeOffsetPosition);

            if (rangeOffset == 0)
            {
                return (ushort)((codePoint + delta) & 0xFFFF);
            }

            int glyphPosition = rangeOffsetPosition + rangeOffset + ((codePoint - start) * 2);
            if (glyphPosition + 2 > _data.Length)
            {
                return 0;
            }

            int glyph = ReadUInt16(glyphPosition);
            return glyph == 0 ? (ushort)0 : (ushort)((glyph + delta) & 0xFFFF);
        }

        return 0;
    }

    private ushort LookupFormat12(int codePoint)
    {
        int groups = (int)ReadUInt32(_cmapOffset + 12);
        int start = _cmapOffset + 16;

        int low = 0;
        int high = groups - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            int record = start + (middle * 12);
            if (record + 12 > _data.Length)
            {
                return 0;
            }

            uint first = ReadUInt32(record);
            uint last = ReadUInt32(record + 4);

            if (codePoint < first)
            {
                high = middle - 1;
            }
            else if (codePoint > last)
            {
                low = middle + 1;
            }
            else
            {
                uint firstGlyph = ReadUInt32(record + 8);
                return (ushort)(firstGlyph + (codePoint - first));
            }
        }

        return 0;
    }

    /// <summary>Advance width in font units.</summary>
    public int AdvanceFor(ushort glyph)
    {
        if (_advanceCache.TryGetValue(glyph, out int cached))
        {
            return cached;
        }

        int advance;
        if (_numberOfHMetrics <= 0)
        {
            advance = UnitsPerEm / 2;
        }
        else
        {
            int index = Math.Min(glyph, _numberOfHMetrics - 1);
            int position = _hmtxOffset + (index * 4);
            advance = position + 2 <= _data.Length ? ReadUInt16(position) : UnitsPerEm / 2;
        }

        _advanceCache[glyph] = advance;
        return advance;
    }

    /// <summary>Advance in thousandths of an em, the unit a document uses.</summary>
    public int ScaledAdvance(ushort glyph) => (int)Math.Round(AdvanceFor(glyph) * 1000.0 / UnitsPerEm);

    private ushort ReadUInt16(int offset) => (ushort)((_data[offset] << 8) | _data[offset + 1]);

    private short ReadInt16(int offset) => (short)((_data[offset] << 8) | _data[offset + 1]);

    private uint ReadUInt32(int offset) =>
        ((uint)_data[offset] << 24) | ((uint)_data[offset + 1] << 16) | ((uint)_data[offset + 2] << 8) | _data[offset + 3];

    private static string Sanitize(string name)
    {
        var clean = new System.Text.StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c))
            {
                clean.Append(c);
            }
        }

        return clean.Length == 0 ? "EmbeddedFont" : clean.ToString();
    }
}
