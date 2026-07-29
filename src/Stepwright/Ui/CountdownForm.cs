using System.Drawing.Drawing2D;
using Stepwright.Native;

namespace Stepwright.Ui;

/// <summary>A short countdown so the person can get to the right window before capture starts.</summary>
public sealed class CountdownForm : Form
{
    private readonly System.Windows.Forms.Timer _tick = new();
    private int _remaining;

    public CountdownForm(int seconds)
    {
        _remaining = Math.Max(1, seconds);

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        ClientSize = new Size(190, 190);
        BackColor = Color.FromArgb(20, 21, 25);
        DoubleBuffered = true;

        // The window itself is the circle. Blending against a transparency colour would
        // leave a coloured fringe around the edge.
        using var shape = new GraphicsPath();
        shape.AddEllipse(0, 0, ClientSize.Width, ClientSize.Height);
        Region = new Region(shape);

        Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Rectangle area = screen.WorkingArea;
        Location = new Point(
            area.Left + ((area.Width - Width) / 2),
            area.Top + ((area.Height - Height) / 2));

        _tick.Interval = 1000;
        _tick.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                _tick.Stop();
                Close();
                return;
            }

            Invalidate();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080 | 0x00000020; // Tool window, transparent to clicks.
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        NativeMethods.ExcludeFromCapture(Handle, true);
        _tick.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(20, 21, 25));

        var circle = new Rectangle(4, 4, Width - 9, Height - 9);
        using (var edge = new Pen(Color.FromArgb(226, 68, 68), 5f))
        {
            graphics.DrawEllipse(edge, circle);
        }

        using var font = new Font("Segoe UI", 68f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var ink = new SolidBrush(Color.White);
        var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        graphics.DrawString(_remaining.ToString(), font, ink, circle, format);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _tick.Dispose();
        base.OnFormClosed(e);
    }
}
