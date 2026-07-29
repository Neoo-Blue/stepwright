using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Stepwright.Config;
using Stepwright.Export.Gif;
using Stepwright.Model;

namespace Stepwright.Render;

/// <summary>
/// Strings the whole guide together into one animation: every step in order, each held long
/// enough to read, with its number, its sentence and a bar showing how far along it is.
///
/// This is the overview you paste into a chat or put at the top of a page. The per step
/// animation in <see cref="StepAnimator"/> is the one that zooms in on a single action.
/// </summary>
public static class GuideAnimator
{
    private static readonly Color Backdrop = Color.FromArgb(24, 26, 31);
    private static readonly Color Ink = Color.FromArgb(238, 240, 245);
    private static readonly Color Quiet = Color.FromArgb(150, 156, 168);

    public static byte[]? Build(
        Guide guide,
        AppSettings settings,
        IProgress<string>? progress = null,
        int maxWidth = 900)
    {
        List<Step> steps = guide.Visible
            .Where(s => s.Kind != StepKind.Heading && s.HasImage)
            .ToList();

        if (steps.Count == 0)
        {
            return null;
        }

        int width = Math.Clamp(maxWidth, 480, 1400);
        int captionHeight = 78;
        int pictureHeight = (int)Math.Round(width * 0.58);
        int height = pictureHeight + captionHeight;

        var pixels = new List<int[]>(steps.Count);
        var delays = new List<int>(steps.Count);

        for (int i = 0; i < steps.Count; i++)
        {
            Step step = steps[i];
            progress?.Report($"Drawing step {i + 1} of {steps.Count}");

            using Bitmap? picture = GuideRenderer.Render(guide, step, settings, width * 2);
            using Bitmap frame = Compose(picture, step, i + 1, steps.Count, width, height, pictureHeight);

            pixels.Add(ReadPixels(frame));
            delays.Add(HoldFor(step));
        }

        progress?.Report("Building the animation...");
        return Encode(pixels, delays, width, height);
    }

    /// <summary>Long enough to read the sentence, and never so long it feels stuck.</summary>
    private static int HoldFor(Step step)
    {
        int words = step.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int centiseconds = 130 + (words * 14);
        return Math.Clamp(centiseconds, 130, 420);
    }

    private static Bitmap Compose(
        Bitmap? picture,
        Step step,
        int number,
        int total,
        int width,
        int height,
        int pictureHeight)
    {
        var frame = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (Graphics graphics = Graphics.FromImage(frame))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(Backdrop);

            if (picture is not null)
            {
                // Fitted rather than filled, so nothing is ever cut off the side.
                double scale = Math.Min(
                    (width - 24) / (double)picture.Width,
                    (pictureHeight - 20) / (double)picture.Height);

                int drawWidth = Math.Max(1, (int)(picture.Width * scale));
                int drawHeight = Math.Max(1, (int)(picture.Height * scale));

                var area = new Rectangle(
                    (width - drawWidth) / 2,
                    10 + ((pictureHeight - 20 - drawHeight) / 2),
                    drawWidth,
                    drawHeight);

                graphics.DrawImage(picture, area);

                using var edge = new Pen(Color.FromArgb(64, 68, 78));
                graphics.DrawRectangle(edge, area);
            }

            var badge = new Rectangle(20, pictureHeight + 16, 26, 26);
            using (var badgeBrush = new SolidBrush(Color.FromArgb(88, 132, 255)))
            {
                graphics.FillEllipse(badgeBrush, badge);
            }

            var centred = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };

            using (var badgeInk = new SolidBrush(Color.White))
            using (var badgeFont = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.DrawString(number.ToString(), badgeFont, badgeInk, badge, centred);
            }

            using (var ink = new SolidBrush(Ink))
            using (var font = new Font("Segoe UI", 16f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                var textArea = new RectangleF(56, pictureHeight + 12, width - 130, 38);
                var wrap = new StringFormat { Trimming = StringTrimming.EllipsisWord };
                graphics.DrawString(step.Text, font, ink, textArea, wrap);
            }

            using (var quiet = new SolidBrush(Quiet))
            using (var small = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                var counter = new RectangleF(width - 72, pictureHeight + 18, 56, 20);
                var right = new StringFormat { Alignment = StringAlignment.Far };
                graphics.DrawString($"{number} of {total}", small, quiet, counter, right);
            }

            // A bar along the very bottom showing how far through the guide this is.
            using (var track = new SolidBrush(Color.FromArgb(48, 52, 61)))
            {
                graphics.FillRectangle(track, 0, height - 4, width, 4);
            }

            using (var done = new SolidBrush(Color.FromArgb(88, 132, 255)))
            {
                graphics.FillRectangle(done, 0, height - 4, width * number / Math.Max(1, total), 4);
            }
        }

        return frame;
    }

    private static int[] ReadPixels(Bitmap frame)
    {
        var pixels = new int[frame.Width * frame.Height];
        BitmapData data = frame.LockBits(
            new Rectangle(0, 0, frame.Width, frame.Height),
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
        var sample = new List<int>(150000);
        int perFrame = Math.Max(1, 150000 / Math.Max(1, pixels.Count));

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
