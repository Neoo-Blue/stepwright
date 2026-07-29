using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Stepwright.Capture;
using Stepwright.Config;
using Stepwright.Export.Gif;
using Stepwright.Model;

namespace Stepwright.Render;

/// <summary>How lively the movement is. The middle setting suits almost everything.</summary>
public enum GifMotion
{
    Gentle,
    Normal,
    Quick,
}

/// <summary>
/// Turns one screenshot into a short animation that starts wide, so the reader can see where
/// they are, and settles on the control that was used.
///
/// It is built entirely from the picture already captured, so there is nothing to time, catch
/// or synchronise. The same step always produces the same animation.
/// </summary>
public static class StepAnimator
{
    /// <summary>True when there is somewhere meaningful to move to.</summary>
    public static bool CanAnimate(Step step)
    {
        if (!step.HasImage)
        {
            return false;
        }

        return step.ClickPoint is not null || (step.ElementArea is { } area && !area.IsEmpty);
    }

    public static byte[]? Build(
        Guide guide,
        Step step,
        AppSettings settings,
        GifMotion motion = GifMotion.Normal,
        int maxWidth = 760)
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
            // Everything is drawn once onto the whole picture, then the animation is only a
            // moving window over that. The marker and any callouts come along for free.
            var whole = new Step
            {
                ClickPoint = step.ClickPoint,
                ElementArea = step.ElementArea,
                WindowArea = step.WindowArea,
                Annotations = step.Annotations,
                ShowClickMarker = step.ShowClickMarker,
                ShowElementOutline = step.ShowElementOutline,
                AutoZoom = false,
                Crop = null,
            };

            using Bitmap composed = StepRenderer.Compose(whole, source, settings.MarkerColor, settings.ZoomPadding);

            (Rectangle start, Rectangle end) = Framings(step, composed.Size, settings.ZoomPadding);
            if (end.Width < 40 || end.Height < 40)
            {
                return null;
            }

            // Both ends share the shape of the finished picture, so nothing squashes on the way.
            start = MatchAspect(start, end.Width / (double)end.Height, composed.Size);

            (int steps, int moveDelay, int holdDelay, int returnSteps) = Timing(motion);

            int width = Math.Min(maxWidth, end.Width);
            int height = Math.Max(1, (int)Math.Round(width * end.Height / (double)end.Width));

            var pixels = new List<int[]>();
            var delays = new List<int>();

            for (int i = 0; i < steps; i++)
            {
                double t = Ease(i / (double)(steps - 1));
                pixels.Add(ReadFrame(composed, Between(start, end, t), width, height));
                delays.Add(i == steps - 1 ? holdDelay : moveDelay);
            }

            for (int i = 1; i <= returnSteps; i++)
            {
                double t = Ease(1 - (i / (double)returnSteps));
                pixels.Add(ReadFrame(composed, Between(start, end, t), width, height));
                delays.Add(i == returnSteps ? Math.Max(40, holdDelay / 3) : moveDelay);
            }

            return Encode(pixels, delays, width, height);
        }
    }

    /// <summary>Where the animation starts and where it settles.</summary>
    private static (Rectangle Start, Rectangle End) Framings(Step step, Size size, int padding)
    {
        Rectangle end = StepRenderer.VariantCrop(step, size, CropVariant.Focus, padding);
        var full = new Rectangle(Point.Empty, size);

        // Start from the window when there is one, because a whole desktop is rarely useful.
        Rectangle start = StepRenderer.VariantCrop(step, size, CropVariant.Window, padding);
        if (start.Width < end.Width * 1.35 || start.Height < end.Height * 1.35)
        {
            start = full;
        }

        // Nothing to move towards, so tighten the ending instead of animating in place.
        if (end.Width > start.Width * 0.85 && end.Height > start.Height * 0.85)
        {
            end = StepRenderer.VariantCrop(step, size, CropVariant.Close, padding);
        }

        return (start, end);
    }

    private static (int Steps, int MoveDelay, int HoldDelay, int ReturnSteps) Timing(GifMotion motion) => motion switch
    {
        GifMotion.Gentle => (14, 7, 220, 8),
        GifMotion.Quick => (8, 4, 130, 5),
        _ => (11, 5, 180, 6),
    };

    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 2 * t * t : 1 - (2 * (1 - t) * (1 - t));
    }

    private static Rectangle Between(Rectangle from, Rectangle to, double t)
    {
        int x = (int)Math.Round(from.X + ((to.X - from.X) * t));
        int y = (int)Math.Round(from.Y + ((to.Y - from.Y) * t));
        int w = (int)Math.Round(from.Width + ((to.Width - from.Width) * t));
        int h = (int)Math.Round(from.Height + ((to.Height - from.Height) * t));
        return new Rectangle(x, y, Math.Max(8, w), Math.Max(8, h));
    }

    /// <summary>Grows a region until it has the required shape, then keeps it on the picture.</summary>
    private static Rectangle MatchAspect(Rectangle area, double aspect, Size bounds)
    {
        int width = area.Width;
        int height = area.Height;

        if (width / (double)height > aspect)
        {
            height = (int)Math.Round(width / aspect);
        }
        else
        {
            width = (int)Math.Round(height * aspect);
        }

        int centreX = area.X + (area.Width / 2);
        int centreY = area.Y + (area.Height / 2);

        width = Math.Min(width, bounds.Width);
        height = Math.Min(height, bounds.Height);

        var result = new Rectangle(centreX - (width / 2), centreY - (height / 2), width, height);

        if (result.X < 0)
        {
            result.X = 0;
        }

        if (result.Y < 0)
        {
            result.Y = 0;
        }

        if (result.Right > bounds.Width)
        {
            result.X = bounds.Width - result.Width;
        }

        if (result.Bottom > bounds.Height)
        {
            result.Y = bounds.Height - result.Height;
        }

        return result;
    }

    private static int[] ReadFrame(Bitmap composed, Rectangle region, int width, int height)
    {
        region = Rectangle.Intersect(region, new Rectangle(Point.Empty, composed.Size));
        if (region.Width < 4 || region.Height < 4)
        {
            region = new Rectangle(Point.Empty, composed.Size);
        }

        using var frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(frame))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(composed, new Rectangle(0, 0, width, height), region, GraphicsUnit.Pixel);
        }

        var pixels = new int[width * height];
        BitmapData data = frame.LockBits(
            new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            frame.UnlockBits(data);
        }

        return pixels;
    }

    private static byte[] Encode(List<int[]> pixels, List<int> delays, int width, int height)
    {
        // One palette for the whole animation, sampled across every picture in it, so the
        // colours cannot shift as the movement goes on.
        var sample = new List<int>(120000);
        int perFrame = Math.Max(1, 120000 / Math.Max(1, pixels.Count));

        foreach (int[] frame in pixels)
        {
            int step = Math.Max(1, frame.Length / perFrame);
            for (int i = 0; i < frame.Length; i += step)
            {
                sample.Add(frame[i]);
            }
        }

        GifPalette palette = GifPalette.Build(sample);

        var frames = new List<GifFrame>(pixels.Count);
        for (int i = 0; i < pixels.Count; i++)
        {
            frames.Add(new GifFrame
            {
                Indices = palette.Map(pixels[i]),
                DelayCentiseconds = delays[i],
            });
        }

        using var output = new MemoryStream();
        GifWriter.Write(output, width, height, palette, frames);
        return output.ToArray();
    }
}
