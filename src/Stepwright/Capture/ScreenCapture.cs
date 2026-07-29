using System.Drawing.Imaging;
using Stepwright.Native;

namespace Stepwright.Capture;

/// <summary>
/// A raw screen grab plus the virtual desktop coordinate its top left corner sits on.
/// One grab can be shared by two steps, so the frame is released rather than disposed
/// and the picture is written to disk only once.
/// </summary>
public sealed class CapturedFrame
{
    private int _references = 1;
    private string? _savedName;

    public required Bitmap Image { get; init; }

    /// <summary>Position of the image inside the virtual desktop, in physical pixels.</summary>
    public required Point Origin { get; init; }

    /// <summary>True when the grab failed and the picture is not worth keeping.</summary>
    public bool Failed { get; init; }

    public Point ToImagePoint(Point screenPoint) => new(screenPoint.X - Origin.X, screenPoint.Y - Origin.Y);

    public Rectangle ToImageRect(Rectangle screenRect) =>
        new(screenRect.X - Origin.X, screenRect.Y - Origin.Y, screenRect.Width, screenRect.Height);

    public void AddReference() => Interlocked.Increment(ref _references);

    /// <summary>
    /// Writes the picture once, however many steps share it, and returns the file name.
    /// </summary>
    public string SaveOnce(string folder, Func<string> nameFactory)
    {
        lock (this)
        {
            if (_savedName is not null)
            {
                return _savedName;
            }

            if (Failed)
            {
                _savedName = string.Empty;
                return _savedName;
            }

            try
            {
                string name = nameFactory();
                ScreenCapture.SavePng(Image, Path.Combine(folder, name));
                _savedName = name;
            }
            catch
            {
                _savedName = string.Empty;
            }

            return _savedName;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) <= 0)
        {
            Image.Dispose();
        }
    }
}

public static class ScreenCapture
{
    /// <summary>
    /// Grabs the monitor under the given point, or the whole desktop when asked.
    /// The copy goes through BitBlt rather than Graphics.CopyFromScreen, because the
    /// managed wrapper rejects the raster operation that includes layered windows.
    /// </summary>
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

        Bitmap? image = GrabRegion(bounds);
        if (image is not null)
        {
            return new CapturedFrame { Image = image, Origin = bounds.Location };
        }

        return new CapturedFrame
        {
            Image = new Bitmap(Math.Max(1, bounds.Width), Math.Max(1, bounds.Height), PixelFormat.Format24bppRgb),
            Origin = bounds.Location,
            Failed = true,
        };
    }

    private static Bitmap? GrabRegion(Rectangle bounds)
    {
        IntPtr screenDc = IntPtr.Zero;
        IntPtr memoryDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr previous = IntPtr.Zero;

        try
        {
            screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                return null;
            }

            memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            bitmap = NativeMethods.CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
            {
                return null;
            }

            previous = NativeMethods.SelectObject(memoryDc, bitmap);

            bool copied = NativeMethods.BitBlt(
                memoryDc,
                0,
                0,
                bounds.Width,
                bounds.Height,
                screenDc,
                bounds.X,
                bounds.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

            if (!copied)
            {
                // The secure desktop and some protected windows refuse the copy.
                return null;
            }

            // FromHbitmap hands back an opaque picture, so the saved file cannot come out blank.
            return Image.FromHbitmap(bitmap);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memoryDc, previous);
            }

            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }

            if (memoryDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memoryDc);
            }

            if (screenDc != IntPtr.Zero)
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    public static Point CursorPosition()
    {
        return NativeMethods.GetCursorPos(out NativeMethods.POINT point)
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
