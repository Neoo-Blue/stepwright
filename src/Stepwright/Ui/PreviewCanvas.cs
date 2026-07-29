using System.Drawing.Drawing2D;

namespace Stepwright.Ui;

public enum CanvasTool
{
    Select,
    Box,
    Arrow,
    Blur,
    Highlight,
    Text,
    Crop,
    Marker,
}

public sealed class RegionEventArgs : EventArgs
{
    /// <summary>Region in the coordinates of the original screenshot.</summary>
    public required Rectangle Region { get; init; }

    public required CanvasTool Tool { get; init; }
}

public sealed class PointEventArgs : EventArgs
{
    public required Point Location { get; init; }
    public required CanvasTool Tool { get; init; }
}

/// <summary>
/// Shows the composed picture for a step and lets the person draw on it.
/// Everything reported back is in the coordinate space of the original screenshot,
/// so a later change of crop keeps the callouts where they belong.
/// </summary>
public sealed class PreviewCanvas : Control
{
    private Bitmap? _image;
    private Point _origin;
    private Rectangle _target;
    private double _scale = 1;
    private double _renderScale = 1;
    private bool _dragging;
    private Point _dragStart;
    private Point _dragNow;

    public PreviewCanvas()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);
        BackColor = Theme.Window;
        Tool = CanvasTool.Select;
    }

    public CanvasTool Tool { get; set; }

    public Color DrawColor { get; set; } = Color.OrangeRed;

    public string EmptyMessage { get; set; } = "No screenshot for this step";

    /// <summary>A quieter second line under the message.</summary>
    public string EmptyHint { get; set; } = string.Empty;

    public event EventHandler<RegionEventArgs>? RegionDrawn;

    public event EventHandler<PointEventArgs>? PointPicked;

    /// <summary>
    /// Hands the canvas a fresh picture. The canvas takes ownership of it.
    /// The origin and the scale describe how the picture relates to the original screenshot,
    /// so anything drawn here can be stored in the coordinates of that screenshot.
    /// </summary>
    public void SetImage(Bitmap? image, Point sourceOrigin, double sourceScale = 1.0)
    {
        _image?.Dispose();
        _image = image;
        _origin = sourceOrigin;
        _renderScale = sourceScale > 0 ? sourceScale : 1.0;
        _dragging = false;
        Invalidate();
    }

    public Bitmap? CurrentImage => _image;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        using (var back = new SolidBrush(Theme.Window))
        {
            graphics.FillRectangle(back, ClientRectangle);
        }

        if (_image is null)
        {
            DrawEmptyState(graphics);
            return;
        }

        Measure();

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // A soft stack of shadows under the picture, so it sits on the surface rather than
        // being pasted onto it.
        for (int spread = 10; spread >= 2; spread -= 2)
        {
            var halo = new Rectangle(
                _target.X - (spread / 2),
                _target.Y - (spread / 2) + 3,
                _target.Width + spread,
                _target.Height + spread);

            Theme.FillRounded(graphics, halo, Color.FromArgb(11, 0, 0, 0), 10 + spread);
        }

        using (GraphicsPath clip = Theme.RoundedRect(_target, 8))
        {
            GraphicsState state = graphics.Save();
            graphics.SetClip(clip);
            graphics.DrawImage(_image, _target);
            graphics.Restore(state);
        }

        Theme.DrawRounded(graphics, _target, Theme.Border, 8);

        if (_dragging)
        {
            DrawLivePreview(graphics);
        }
    }

    private void DrawLivePreview(Graphics graphics)
    {
        Rectangle rect = Normalize(_dragStart, _dragNow);

        switch (Tool)
        {
            case CanvasTool.Crop:
            {
                using var dim = new SolidBrush(Color.FromArgb(130, 0, 0, 0));
                using (var region = new Region(_target))
                {
                    region.Exclude(rect);
                    graphics.FillRegion(dim, region);
                }

                using var pen = new Pen(Color.White, 1.5f) { DashStyle = DashStyle.Dash };
                graphics.DrawRectangle(pen, rect);
                break;
            }

            case CanvasTool.Blur:
            {
                using var fill = new SolidBrush(Color.FromArgb(120, 40, 44, 52));
                using var pen = new Pen(Color.FromArgb(200, 200, 205, 215), 1.5f) { DashStyle = DashStyle.Dash };
                graphics.FillRectangle(fill, rect);
                graphics.DrawRectangle(pen, rect);
                break;
            }

            case CanvasTool.Highlight:
            {
                using var fill = new SolidBrush(Color.FromArgb(80, DrawColor));
                graphics.FillRectangle(fill, rect);
                break;
            }

            case CanvasTool.Arrow:
            {
                using var pen = new Pen(DrawColor, 4f)
                {
                    StartCap = LineCap.Round,
                    CustomEndCap = new AdjustableArrowCap(4f, 5f),
                };
                graphics.DrawLine(pen, _dragStart, _dragNow);
                break;
            }

            default:
            {
                using var pen = new Pen(DrawColor, 3f);
                graphics.DrawRectangle(pen, rect);
                break;
            }
        }
    }

    /// <summary>What the canvas shows before anything has been recorded.</summary>
    private void DrawEmptyState(Graphics graphics)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var centre = new Point(Width / 2, (Height / 2) - 26);
        var plate = new Rectangle(centre.X - 46, centre.Y - 46, 92, 92);
        Theme.FillRounded(graphics, plate, Theme.Panel, 22);

        // A small window shape, drawn rather than shipped as a picture.
        var glass = new Rectangle(plate.X + 22, plate.Y + 26, 48, 36);
        Theme.FillRounded(graphics, glass, Theme.Raised, 5);
        Theme.DrawRounded(graphics, glass, Theme.Border, 5);

        using (var bar = new SolidBrush(Theme.Border))
        {
            graphics.FillRectangle(bar, glass.X + 1, glass.Y + 1, glass.Width - 2, 7);
        }

        using (var dot = new SolidBrush(Theme.Accent))
        {
            graphics.FillEllipse(dot, glass.Right - 16, glass.Bottom - 15, 9, 9);
        }

        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };

        using (var ink = new SolidBrush(Theme.Text))
        {
            graphics.DrawString(
                EmptyMessage,
                Theme.UiTitle,
                ink,
                new RectangleF(0, plate.Bottom + 18, Width, 30),
                format);
        }

        if (!string.IsNullOrEmpty(EmptyHint))
        {
            using var muted = new SolidBrush(Theme.Muted);
            graphics.DrawString(
                EmptyHint,
                Theme.Ui,
                muted,
                new RectangleF(0, plate.Bottom + 46, Width, 40),
                format);
        }
    }

    private void Measure()
    {
        if (_image is null)
        {
            return;
        }

        int margin = 14;
        double available = Math.Max(1, Width - (margin * 2));
        double availableHeight = Math.Max(1, Height - (margin * 2));
        _scale = Math.Min(available / _image.Width, availableHeight / _image.Height);
        _scale = Math.Min(_scale, 1.0);

        int width = Math.Max(1, (int)(_image.Width * _scale));
        int height = Math.Max(1, (int)(_image.Height * _scale));
        _target = new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
    }

    private static Rectangle Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>Maps a point on screen to a pixel of the original screenshot.</summary>
    private Point ToSource(Point canvasPoint)
    {
        if (_image is null || _scale <= 0)
        {
            return Point.Empty;
        }

        double factor = _scale * _renderScale;
        if (factor <= 0)
        {
            return Point.Empty;
        }

        int x = (int)Math.Round((canvasPoint.X - _target.X) / factor) + _origin.X;
        int y = (int)Math.Round((canvasPoint.Y - _target.Y) / factor) + _origin.Y;
        return new Point(x, y);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Measure();
        if (_image is null || e.Button != MouseButtons.Left || !_target.Contains(e.Location))
        {
            return;
        }

        if (Tool is CanvasTool.Select or CanvasTool.Marker or CanvasTool.Text)
        {
            PointPicked?.Invoke(this, new PointEventArgs { Location = ToSource(e.Location), Tool = Tool });
            if (Tool != CanvasTool.Select)
            {
                return;
            }
        }

        if (Tool == CanvasTool.Select)
        {
            return;
        }

        _dragging = true;
        _dragStart = e.Location;
        _dragNow = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Measure();
        if (!_dragging)
        {
            Cursor = Tool == CanvasTool.Select ? Cursors.Default : Cursors.Cross;
            return;
        }

        _dragNow = new Point(
            Math.Clamp(e.X, _target.Left, _target.Right),
            Math.Clamp(e.Y, _target.Top, _target.Bottom));
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        Measure();
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        Invalidate();

        Point start = ToSource(_dragStart);
        Point end = ToSource(_dragNow);

        Rectangle region = Tool == CanvasTool.Arrow
            ? new Rectangle(start.X, start.Y, end.X - start.X, end.Y - start.Y)
            : new Rectangle(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Abs(end.X - start.X),
                Math.Abs(end.Y - start.Y));

        if (Tool != CanvasTool.Arrow && (region.Width < 6 || region.Height < 6))
        {
            return;
        }

        RegionDrawn?.Invoke(this, new RegionEventArgs { Region = region, Tool = Tool });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
            _image = null;
        }

        base.Dispose(disposing);
    }
}
