using System.Text;

namespace Stepwright.Export.Gif;

/// <summary>One picture in an animation, already reduced to palette slots.</summary>
public sealed class GifFrame
{
    public required byte[] Indices { get; init; }

    /// <summary>How long this picture stays on screen, in hundredths of a second.</summary>
    public required int DelayCentiseconds { get; init; }
}

/// <summary>
/// Writes an animated gif by hand: header, colour table, the looping marker that every
/// viewer understands, and each picture compressed the way the format requires.
///
/// There is no dependency on the platform, so the output can be produced and checked on any
/// machine rather than only where the app runs.
/// </summary>
public static class GifWriter
{
    public static void Write(
        Stream output,
        int width,
        int height,
        GifPalette palette,
        IReadOnlyList<GifFrame> frames,
        int loops = 0)
    {
        if (frames.Count == 0)
        {
            throw new ArgumentException("An animation needs at least one picture.", nameof(frames));
        }

        Ascii(output, "GIF89a");

        // Logical screen: the size, then a byte saying a global colour table of 256 follows.
        Short(output, width);
        Short(output, height);
        output.WriteByte(0xF7);
        output.WriteByte(0x00);
        output.WriteByte(0x00);

        output.Write(palette.Table, 0, 768);

        // The looping marker. Zero means forever.
        output.WriteByte(0x21);
        output.WriteByte(0xFF);
        output.WriteByte(0x0B);
        Ascii(output, "NETSCAPE2.0");
        output.WriteByte(0x03);
        output.WriteByte(0x01);
        Short(output, loops);
        output.WriteByte(0x00);

        foreach (GifFrame frame in frames)
        {
            // Graphic control: keep the previous picture in place underneath, and hold for
            // the requested time. No colour is transparent.
            output.WriteByte(0x21);
            output.WriteByte(0xF9);
            output.WriteByte(0x04);
            output.WriteByte(0x04);
            Short(output, Math.Clamp(frame.DelayCentiseconds, 2, 65535));
            output.WriteByte(0x00);
            output.WriteByte(0x00);

            output.WriteByte(0x2C);
            Short(output, 0);
            Short(output, 0);
            Short(output, width);
            Short(output, height);
            output.WriteByte(0x00);

            Compress(output, frame.Indices);
        }

        output.WriteByte(0x3B);
    }

    /// <summary>
    /// The compression the format requires: codes of a growing width, packed into bytes from
    /// the low bit upwards, and handed over in blocks of at most 255 bytes.
    /// </summary>
    private static void Compress(Stream output, byte[] indices)
    {
        const int MinimumCodeSize = 8;
        const int ClearCode = 1 << MinimumCodeSize;
        const int EndCode = ClearCode + 1;

        output.WriteByte(MinimumCodeSize);

        var blocks = new BlockWriter(output);
        var dictionary = new Dictionary<int, int>(4096);

        int codeSize = MinimumCodeSize + 1;
        int nextCode = EndCode + 1;

        blocks.WriteCode(ClearCode, codeSize);

        if (indices.Length == 0)
        {
            blocks.WriteCode(EndCode, codeSize);
            blocks.Flush();
            return;
        }

        int current = indices[0];

        for (int i = 1; i < indices.Length; i++)
        {
            int next = indices[i];
            int key = (current << 8) | next;

            if (dictionary.TryGetValue(key, out int combined))
            {
                current = combined;
                continue;
            }

            blocks.WriteCode(current, codeSize);

            if (nextCode < 4096)
            {
                dictionary[key] = nextCode;

                if (nextCode >= 1 << codeSize && codeSize < 12)
                {
                    codeSize++;
                }

                nextCode++;
            }
            else
            {
                // The dictionary is full, so both sides start again from a known state.
                blocks.WriteCode(ClearCode, codeSize);
                dictionary.Clear();
                codeSize = MinimumCodeSize + 1;
                nextCode = EndCode + 1;
            }

            current = next;
        }

        blocks.WriteCode(current, codeSize);
        blocks.WriteCode(EndCode, codeSize);
        blocks.Flush();
    }

    private static void Ascii(Stream output, string text)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(text);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void Short(Stream output, int value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)((value >> 8) & 0xFF));
    }

    /// <summary>Packs codes into bits, then bits into the blocks the format expects.</summary>
    private sealed class BlockWriter
    {
        private readonly Stream _output;
        private readonly byte[] _block = new byte[255];
        private int _blockLength;
        private int _bits;
        private int _bitCount;

        public BlockWriter(Stream output) => _output = output;

        public void WriteCode(int code, int codeSize)
        {
            _bits |= code << _bitCount;
            _bitCount += codeSize;

            while (_bitCount >= 8)
            {
                Add((byte)(_bits & 0xFF));
                _bits >>= 8;
                _bitCount -= 8;
            }
        }

        public void Flush()
        {
            if (_bitCount > 0)
            {
                Add((byte)(_bits & 0xFF));
                _bits = 0;
                _bitCount = 0;
            }

            WriteBlock();
            _output.WriteByte(0x00);
        }

        private void Add(byte value)
        {
            _block[_blockLength++] = value;
            if (_blockLength == 255)
            {
                WriteBlock();
            }
        }

        private void WriteBlock()
        {
            if (_blockLength == 0)
            {
                return;
            }

            _output.WriteByte((byte)_blockLength);
            _output.Write(_block, 0, _blockLength);
            _blockLength = 0;
        }
    }
}
