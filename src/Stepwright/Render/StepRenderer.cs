using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Stepwright.Model;

namespace Stepwright.Render;

/// <summary>Draws the finished picture for a step: crop, redactions, callouts and the click marker.</summary>
public static class StepRenderer
{
    public static Color Parse(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        string clean = hex.TrimStart('#');
        try
        {
            if (clean.Length == 6)
            {
                return Color.FromArgb(
                    Convert.ToInt32(clean[..2], 16),
                    Convert.ToInt32(clean.Substring(2, 2), 16),
                    Convert.ToInt32(clean.Substring(4, 2), 16));
            }
        }
        catch
        {
            // Fall through to the default colour.
        }

        return fallback;
    }

    public static string ToHex(Color color) => $"{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Size of the pill a text callout will occupy. The editor stores this so the label can
    /// be clicked anywhere on it, not only at the corner it was placed from.
    /// </summary>
    public static Size MeasureLabel(string text, int thickness)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Size(40, 30);
        }

        using var scratch = new Bitmap(1, 1);
        using Graphics graphics = Graphics.FromImage(scratch);
        using var font = new Font("Segoe UI", Math.Max(14, thickness * 5), FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF measured = graphics.MeasureString(text, font);
        return new Size((int)measured.Width + 24, (int)measured.Height + 14);
    }

    /// <summary>Region of the source image the step should display.</summary>
    public static Rectangle EffectiveCrop(Step step, Size imageSize, int padding)
    {
        var full = new Rectangle(Point.Empty, imageSize);

        if (step.Crop is { } manual && !manual.IsEmpty)
        {
            return Rectangle.Intersect(manual.Rect, full);
        }

        if (!step.AutoZoom)
        {
            return full;
        }

        Rectangle anchor = Rectangle.Empty;
        if (step.ElementArea is { } area && !area.IsEmpty)
        {
            Rectangle candidate = area.Rect;
            bool sane = candidate.Width > 4
                && candidate.Height > 4
                && candidate.Width < imageSize.Width * 0.72
                && candidate.Height < imageSize.Height * 0.72;
            if (sane)
            {
                anchor = candidate;
            }
        }

        if (anchor.IsEmpty && step.ClickPoint is { } click)
        {
            anchor = new Rectangle(click.X - 50, click.Y - 50, 100, 100);
        }

        if (anchor.IsEmpty)
        {
            return full;
        }

        int padX = Math.Max(80, padding);
        int padY = (int)(padX * 0.62);
        Rectangle box = Rectangle.Inflate(anchor, padX, padY);

        // Keep the shape of the source so every exported picture lines up.
        double aspect = imageSize.Width / (double)Math.Max(1, imageSize.Height);
        int width = Math.Max(box.Width, (int)(box.Height * aspect));
        int height = Math.Max(box.Height, (int)(width / aspect));
        width = (int)(height * aspect);

        var center = new Point(anchor.X + (anchor.Width / 2), anchor.Y + (anchor.Height / 2));
        var result = new Rectangle(center.X - (width / 2), center.Y - (height / 2), width, height);

        if (result.Width >= imageSize.Width * 0.86 || result.Height >= imageSize.Height * 0.86)
        {
            return full;
        }

        if (result.X < 0)
        {
            result.X = 0;
        }

        if (result.Y < 0)
        {
            result.Y = 0;
        }

        if (result.Right > imageSize.Width)
        {
            result.X = imageSize.Width - result.Width;
        }

        if (result.Bottom > imageSize.Height)
        {
            result.Y = imageSize.Height - result.Height;
        }

        return Rectangle.Intersect(result, full);
    }

    /// <summary>Builds the picture for a step. The caller owns the returned bitmap.</summary>
    public static Bitmap Compose(Step step, Bitmap source, string markerColorHex, int padding)
    {
        Rectangle crop = EffectiveCrop(step, source.Size, padding);
        if (crop.Width <= 0 || crop.Height <= 0)
        {
            crop = new Rectangle(Point.Empty, source.Size);
        }

        var output = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(output))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            graphics.DrawImage(
                source,
                new Rectangle(0, 0, crop.Width, crop.Height),
                crop,
                GraphicsUnit.Pixel);

            // Redactions come first so nothing sensitive can sit above a callout.
            foreach (Annotation annotation in step.Annotations.Where(a => a.Kind == AnnotationKind.Blur))
            {
                Rectangle area = Shift(annotation.Area.Rect, crop);
                Pixelate(graphics, source, area, crop);
            }

            if (step.ShowElementOutline && step.ElementArea is { } element && !element.IsEmpty)
            {
                DrawElementOutline(graphics, Shift(element.Rect, crop), Parse(markerColorHex, Color.OrangeRed));
            }

            foreach (Annotation annotation in step.Annotations.Where(a => a.Kind != AnnotationKind.Blur))
            {
                DrawAnnotation(graphics, annotation, crop);
            }

            if (step.ShowClickMarker && step.ClickPoint is { } click)
            {
                DrawClickMarker(
                    graphics,
                    new Point(click.X - crop.X, click.Y - crop.Y),
                    Parse(markerColorHex, Color.OrangeRed));
            }
        }

        return output;
    }

    private static Rectangle Shift(Rectangle rect, Rectangle crop) =>
        new(rect.X - crop.X, rect.Y - crop.Y, rect.Width, rect.Height);

    private static void DrawElementOutline(Graphics graphics, Rectangle area, Color color)
    {
        if (area.Width <= 1 || area.Height <= 1)
        {
            return;
        }

        Rectangle padded = Rectangle.Inflate(area, 4, 4);
        using var glow = new Pen(Color.FromArgb(70, color), 9f);
        using var pen = new Pen(Color.FromArgb(235, color), 3f);
        using GraphicsPath path = RoundedRect(padded, 8);
        graphics.DrawPath(glow, path);
        graphics.DrawPath(pen, path);
    }

    private static void DrawClickMarker(Graphics graphics, Point center, Color color)
    {
        int outer = 34;
        using (var halo = new SolidBrush(Color.FromArgb(46, color)))
        {
            graphics.FillEllipse(halo, center.X - outer, center.Y - outer, outer * 2, outer * 2);
        }

        int ring = 20;
        using (var pen = new Pen(Color.FromArgb(245, color), 4f))
        {
            graphics.DrawEllipse(pen, center.X - ring, center.Y - ring, ring * 2, ring * 2);
        }

        using (var dot = new SolidBrush(Color.FromArgb(245, color)))
        {
            graphics.FillEllipse(dot, center.X - 5, center.Y - 5, 10, 10);
        }
    }

    private static void DrawAnnotation(Graphics graphics, Annotation annotation, Rectangle crop)
    {
        Rectangle area = Shift(annotation.Area.Rect, crop);
        Color color = Parse(annotation.Color, Color.OrangeRed);
        float thickness = Math.Max(2, annotation.Thickness);

        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
            {
                using var pen = new Pen(color, thickness);
                using GraphicsPath path = RoundedRect(Normalize(area), 6);
                graphics.DrawPath(pen, path);
                break;
            }

            case AnnotationKind.Highlight:
            {
                using var brush = new SolidBrush(Color.FromArgb(70, color));
                graphics.FillRectangle(brush, Normalize(area));
                break;
            }

            case AnnotationKind.Arrow:
            {
                using var pen = new Pen(color, thickness + 1);
                pen.StartCap = LineCap.Round;
                pen.CustomEndCap = new AdjustableArrowCap(4f, 5f);
                graphics.DrawLine(pen, area.X, area.Y, area.X + area.Width, area.Y + area.Height);
                break;
            }

            case AnnotationKind.Number:
            {
                int size = Math.Max(28, Math.Min(area.Width, area.Height));
                var circle = new Rectangle(area.X, area.Y, size, size);
                using var fill = new SolidBrush(color);
                using var font = new Font("Segoe UI", size * 0.46f, FontStyle.Bold, GraphicsUnit.Pixel);
                using var text = new SolidBrush(Color.White);
                graphics.FillEllipse(fill, circle);
                var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                graphics.DrawString(annotation.Number.ToString(), font, text, circle, format);
                break;
            }

            case AnnotationKind.Text:
            {
                if (string.IsNullOrWhiteSpace(annotation.Text))
                {
                    break;
                }

                using var font = new Font("Segoe UI", Math.Max(14, annotation.Thickness * 5), FontStyle.Bold, GraphicsUnit.Pixel);
                SizeF measured = graphics.MeasureString(annotation.Text, font);
                var pill = new Rectangle(area.X, area.Y, (int)measured.Width + 24, (int)measured.Height + 14);
                using var back = new SolidBrush(color);
                using GraphicsPath path = RoundedRect(pill, 8);
                graphics.FillPath(back, path);
                using var ink = new SolidBrush(Color.White);
                graphics.DrawString(annotation.Text, font, ink, pill.X + 12, pill.Y + 7);
                break;
            }
        }
    }

    private static Rectangle Normalize(Rectangle rect)
    {
        int x = rect.Width < 0 ? rect.X + rect.Width : rect.X;
        int y = rect.Height < 0 ? rect.Y + rect.Height : rect.Y;
        return new Rectangle(x, y, Math.Abs(rect.Width), Math.Abs(rect.Height));
    }

    /// <summary>
    /// Blocks out a region. The pixels are read from the original screenshot rather than from
    /// the surface being drawn on, because drawing already issued to that surface is not
    /// guaranteed to be visible to a read. A redaction that samples stale pixels would not
    /// redact anything, so this reads from a source nothing has touched.
    /// </summary>
    private static void Pixelate(Graphics graphics, Bitmap source, Rectangle area, Rectangle crop)
    {
        Rectangle region = Rectangle.Intersect(Normalize(area), new Rectangle(0, 0, crop.Width, crop.Height));
        if (region.Width < 2 || region.Height < 2)
        {
            return;
        }

        var sourceRegion = new Rectangle(region.X + crop.X, region.Y + crop.Y, region.Width, region.Height);
        sourceRegion = Rectangle.Intersect(sourceRegion, new Rectangle(Point.Empty, source.Size));
        if (sourceRegion.Width < 2 || sourceRegion.Height < 2)
        {
            return;
        }

        int blocks = Math.Max(4, Math.Min(24, region.Width / 14));
        int smallWidth = Math.Max(1, blocks);
        int smallHeight = Math.Max(1, region.Height * blocks / Math.Max(1, region.Width));

        using var small = new Bitmap(smallWidth, Math.Max(1, smallHeight), PixelFormat.Format32bppArgb);
        using (Graphics shrink = Graphics.FromImage(small))
        {
            shrink.InterpolationMode = InterpolationMode.HighQualityBilinear;
            shrink.DrawImage(source, new Rectangle(0, 0, small.Width, small.Height), sourceRegion, GraphicsUnit.Pixel);
        }

        InterpolationMode previous = graphics.InterpolationMode;
        PixelOffsetMode previousOffset = graphics.PixelOffsetMode;
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        graphics.DrawImage(small, region);
        graphics.InterpolationMode = previous;
        graphics.PixelOffsetMode = previousOffset;
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Scales a bitmap so its longest edge matches the requested size.</summary>
    public static Bitmap Resize(Bitmap source, int maxWidth, int maxHeight)
    {
        double scale = Math.Min(maxWidth / (double)source.Width, maxHeight / (double)source.Height);
        if (scale >= 1)
        {
            return new Bitmap(source);
        }

        int width = Math.Max(1, (int)(source.Width * scale));
        int height = Math.Max(1, (int)(source.Height * scale));
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }
}
