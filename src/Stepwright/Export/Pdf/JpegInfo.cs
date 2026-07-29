namespace Stepwright.Export.Pdf;

/// <summary>
/// Size and colour layout of a jpeg, read from the frame header. A document embeds the
/// compressed bytes as they are, so it has to state the dimensions and the colour space
/// itself rather than decoding the picture.
/// </summary>
public sealed class JpegInfo
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int Components { get; init; }

    public string ColorSpace => Components switch
    {
        1 => "/DeviceGray",
        4 => "/DeviceCMYK",
        _ => "/DeviceRGB",
    };

    public static JpegInfo? Read(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return null;
        }

        int position = 2;
        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte marker = data[position + 1];
            position += 2;

            // Padding and standalone markers carry no length.
            if (marker == 0xFF)
            {
                position--;
                continue;
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD9)
            {
                continue;
            }

            if (position + 1 >= data.Length)
            {
                return null;
            }

            int length = (data[position] << 8) | data[position + 1];
            if (length < 2 || position + length > data.Length)
            {
                return null;
            }

            // Every start of frame marker except the two that carry no picture geometry.
            bool isFrame = marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isFrame)
            {
                if (position + 7 >= data.Length)
                {
                    return null;
                }

                int height = (data[position + 3] << 8) | data[position + 4];
                int width = (data[position + 5] << 8) | data[position + 6];
                int components = data[position + 7];

                if (width <= 0 || height <= 0)
                {
                    return null;
                }

                return new JpegInfo { Width = width, Height = height, Components = components };
            }

            position += length;
        }

        return null;
    }
}
