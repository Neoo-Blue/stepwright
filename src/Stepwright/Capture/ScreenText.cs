using System.Drawing.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Stepwright.Capture;

/// <summary>
/// Reads the words off a screenshot using the text recognition built into Windows.
///
/// This exists for the services that cannot be shown a picture. Sending the picture is always
/// better where it is allowed, but where it is not, the words on the screen are most of what
/// the picture was carrying: the label on the button, the heading of the dialog, the name of
/// the field. Nothing leaves the machine to obtain them.
///
/// It matters twice over inside a remote session, where the accessibility tree is blind and
/// the words on screen are the only description of the far machine that exists.
/// </summary>
public static class ScreenText
{
    private static OcrEngine? _engine;
    private static bool _tried;

    /// <summary>True when this copy of Windows can recognise text in the person's languages.</summary>
    public static bool Available => Engine() is not null;

    private static OcrEngine? Engine()
    {
        if (_tried)
        {
            return _engine;
        }

        _tried = true;

        try
        {
            _engine = OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch
        {
            // A language pack that cannot recognise text is not a failure worth reporting: the
            // assistant simply carries on with what the recorder wrote.
            _engine = null;
        }

        return _engine;
    }

    /// <summary>
    /// The words on a picture, in reading order, or nothing when they cannot be read. Lines are
    /// kept because a line is usually one label, and a wall of loose words is worth less to a
    /// reader of the result than a handful of real phrases.
    /// </summary>
    public static async Task<string> ReadAsync(Bitmap picture, int maxWords = 220)
    {
        OcrEngine? engine = Engine();

        if (engine is null || picture.Width < 8 || picture.Height < 8)
        {
            return string.Empty;
        }

        try
        {
            using SoftwareBitmap software = await ToSoftwareAsync(picture).ConfigureAwait(false);
            OcrResult result = await engine.RecognizeAsync(software);

            var lines = new List<string>();
            int words = 0;

            foreach (OcrLine line in result.Lines)
            {
                string text = line.Text.Trim();

                if (text.Length == 0)
                {
                    continue;
                }

                lines.Add(text);
                words += line.Words.Count;

                if (words >= maxWords)
                {
                    break;
                }
            }

            return string.Join(" | ", lines);
        }
        catch
        {
            // Recognition is a nicety. A picture it cannot read leaves the step as it was.
            return string.Empty;
        }
    }

    /// <summary>
    /// Windows recognises text from its own picture type, so the bitmap is handed over as a
    /// stream of png rather than by pointer, which keeps this free of unsafe code.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareAsync(Bitmap picture)
    {
        byte[] bytes;

        using (var buffer = new MemoryStream())
        {
            picture.Save(buffer, ImageFormat.Png);
            bytes = buffer.ToArray();
        }

        var stream = new InMemoryRandomAccessStream();

        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);

        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync();
    }
}
