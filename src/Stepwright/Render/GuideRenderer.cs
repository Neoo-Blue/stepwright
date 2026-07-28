using System.Drawing.Imaging;
using Stepwright.Capture;
using Stepwright.Config;
using Stepwright.Model;

namespace Stepwright.Render;

/// <summary>Produces the finished picture for a step, ready for the editor or an export.</summary>
/// <summary>The finished picture plus how it maps back onto the original screenshot.</summary>
public sealed class RenderedStep
{
    public required Bitmap Image { get; init; }

    /// <summary>Top left corner of the visible region inside the original screenshot.</summary>
    public required Point Origin { get; init; }

    /// <summary>Picture pixels per screenshot pixel, which is below one when the picture was shrunk.</summary>
    public required double Scale { get; init; }
}

public static class GuideRenderer
{
    public static RenderedStep? RenderDetailed(Guide guide, Step step, AppSettings settings, int maxWidth = 1600)
    {
        string path = guide.ImagePath(step);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        Bitmap source;
        try
        {
            source = ScreenCapture.LoadUnlocked(path);
        }
        catch
        {
            return null;
        }

        using (source)
        {
            Rectangle crop = StepRenderer.EffectiveCrop(step, source.Size, settings.ZoomPadding);
            Bitmap composed = StepRenderer.Compose(step, source, settings.MarkerColor, settings.ZoomPadding);

            if (composed.Width <= maxWidth)
            {
                return new RenderedStep { Image = composed, Origin = crop.Location, Scale = 1.0 };
            }

            using (composed)
            {
                Bitmap smaller = StepRenderer.Resize(composed, maxWidth, maxWidth * 4);
                return new RenderedStep
                {
                    Image = smaller,
                    Origin = crop.Location,
                    Scale = smaller.Width / (double)Math.Max(1, composed.Width),
                };
            }
        }
    }

    public static Bitmap? Render(Guide guide, Step step, AppSettings settings, int maxWidth = 1600) =>
        RenderDetailed(guide, step, settings, maxWidth)?.Image;

    public static byte[]? RenderPng(Guide guide, Step step, AppSettings settings, int maxWidth = 1600)
    {
        using Bitmap? bitmap = Render(guide, step, settings, maxWidth);
        if (bitmap is null)
        {
            return null;
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Jpeg keeps large documents small enough to paste into a knowledge base.</summary>
    public static byte[]? RenderJpeg(Guide guide, Step step, AppSettings settings, int maxWidth, long quality)
    {
        using Bitmap? bitmap = Render(guide, step, settings, maxWidth);
        if (bitmap is null)
        {
            return null;
        }

        ImageCodecInfo? encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.MimeType == "image/jpeg");
        if (encoder is null)
        {
            return null;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);

        using var flattened = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(flattened))
        {
            graphics.Clear(Color.White);
            graphics.DrawImage(bitmap, 0, 0, bitmap.Width, bitmap.Height);
        }

        using var stream = new MemoryStream();
        flattened.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}
