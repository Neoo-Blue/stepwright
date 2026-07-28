using System.Drawing.Imaging;

namespace Stepwright.Capture;

/// <summary>A raw screen grab plus the virtual desktop coordinate its top left corner sits on.</summary>
public sealed class CapturedFrame : IDisposable
{
    public required Bitmap Image { get; init; }

    /// <summary>Position of the image inside the virtual desktop, in physical pixels.</summary>
    public required Point Origin { get; init; }

    public Point ToImagePoint(Point screenPoint) => new(screenPoint.X - Origin.X, screenPoint.Y - Origin.Y);

    public Rectangle ToImageRect(Rectangle screenRect) =>
        new(screenRect.X - Origin.X, screenRect.Y - Origin.Y, screenRect.Width, screenRect.Height);

    public void Dispose() => Image.Dispose();
}

public static class ScreenCapture
{
    // SourceCopy combined with CaptureBlt so layered windows, menus and tooltips are included.
    private const CopyPixelOperation CaptureOperation =
        (CopyPixelOperation)(0x00CC0020 | 0x40000000);

    /// <summary>Grabs the monitor under the given point, or the whole desktop when asked.</summary>
    public static CapturedFrame Grab(Point screenPoint, bool allMonitors)
    {
        Rectangle bounds;
        if (allMonitors)
        {
            bounds = SystemInformation.VirtualScreen;
        }
        else
        {
            Screen screen = Screen.FromPoint(screenPoint) ?? Screen.PrimaryScreen!;
            bounds = screen.Bounds;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            bounds = new Rectangle(0, 0, 1920, 1080);
        }

        var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size, CaptureOperation);
        }
        catch
        {
            // A secure desktop or a locked session refuses the copy. Keep whatever was written.
        }

        return new CapturedFrame { Image = bitmap, Origin = bounds.Location };
    }

    public static Point CursorPosition()
    {
        return Native.NativeMethods.GetCursorPos(out Native.NativeMethods.POINT point)
            ? new Point(point.X, point.Y)
            : Cursor.Position;
    }

    public static void SavePng(Bitmap bitmap, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    /// <summary>Loads a bitmap without keeping the file locked, so the editor can rewrite it.</summary>
    public static Bitmap LoadUnlocked(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var temporary = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        return new Bitmap(temporary);
    }
}
