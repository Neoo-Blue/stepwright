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
    Number,
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

    public string EmptyMessage { get; set; } = "No screenshot for this step.";

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
            using var brush = new SolidBrush(Theme.Muted);
            var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString(EmptyMessage, Theme.Ui, brush, ClientRectangle, format);
            return;
        }

        Measure();

        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var shadow = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
        {
            graphics.FillRectangle(shadow, _target.X + 3, _target.Y + 4, _target.Width, _target.Height);
        }

        graphics.DrawImage(_image, _target);

        using (var frame = new Pen(Theme.Border))
        {
            graphics.DrawRectangle(frame, _target.X, _target.Y, _target.Width - 1, _target.Height - 1);
        }

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
                var region = new Region(_target);
                region.Exclude(rect);
                graphics.FillRegion(dim, region);
                region.Dispose();
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
        if (_image is null || e.Button != MouseButtons.Left || !_target.Contains(e.Location))
        {
            return;
        }

        if (Tool is CanvasTool.Select or CanvasTool.Marker or CanvasTool.Text or CanvasTool.Number)
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
