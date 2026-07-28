using System.Drawing.Drawing2D;
using Stepwright.Native;

namespace Stepwright.Ui;

/// <summary>
/// The small floating bar that stays on top while a recording runs.
/// It asks Windows to leave it out of screen captures so it never lands in a screenshot.
/// </summary>
public sealed class RecorderBar : Form
{
    private readonly Label _status = new();
    private readonly Button _pause = new();
    private readonly Button _shot = new();
    private readonly Button _finish = new();
    private readonly System.Windows.Forms.Timer _tick = new();
    private bool _paused;
    private Point _grab;
    private bool _moving;

    public RecorderBar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(20, 21, 25);
        ClientSize = new Size(430, 56);
        DoubleBuffered = true;
        KeyPreview = true;

        _status.AutoSize = false;
        _status.Bounds = new Rectangle(46, 0, 150, 56);
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.FromArgb(236, 238, 242);
        _status.Font = new Font("Segoe UI", 9.75f, FontStyle.Regular);
        _status.BackColor = Color.Transparent;
        _status.Text = "00:00   0 steps";

        SetUpButton(_shot, "Capture", 196);
        SetUpButton(_pause, "Pause", 268);
        SetUpButton(_finish, "Finish", 344);
        _finish.BackColor = Color.FromArgb(226, 68, 68);
        _finish.ForeColor = Color.White;
        _finish.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 92, 92);

        Controls.Add(_status);
        Controls.Add(_shot);
        Controls.Add(_pause);
        Controls.Add(_finish);

        _shot.Click += (_, _) => ShotRequested?.Invoke(this, EventArgs.Empty);
        _pause.Click += (_, _) => PauseRequested?.Invoke(this, EventArgs.Empty);
        _finish.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);

        MouseDown += OnBarMouseDown;
        MouseMove += OnBarMouseMove;
        MouseUp += (_, _) => _moving = false;
        _status.MouseDown += OnBarMouseDown;
        _status.MouseMove += OnBarMouseMove;
        _status.MouseUp += (_, _) => _moving = false;

        _tick.Interval = 500;
        _tick.Tick += (_, _) => UpdateStatus();
        _tick.Start();
    }

    public event EventHandler? PauseRequested;

    public event EventHandler? StopRequested;

    public event EventHandler? ShotRequested;

    public Func<TimeSpan>? ElapsedSource { get; set; }

    public Func<int>? StepCountSource { get; set; }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _pause.Text = value ? "Resume" : "Pause";
            Invalidate();
        }
    }

    private static void SetUpButton(Button button, string text, int x)
    {
        button.Text = text;
        button.Bounds = new Rectangle(x, 12, text == "Finish" ? 74 : 66, 32);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Color.FromArgb(44, 47, 55);
        button.ForeColor = Color.FromArgb(232, 234, 240);
        button.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        button.TabStop = false;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 64, 74);
    }

    private void OnBarMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _moving = true;
        _grab = e.Location;
        if (sender == _status)
        {
            _grab = new Point(e.X + _status.Left, e.Y + _status.Top);
        }
    }

    private void OnBarMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_moving)
        {
            return;
        }

        Point point = sender == _status
            ? new Point(e.X + _status.Left, e.Y + _status.Top)
            : e.Location;

        Location = new Point(Location.X + point.X - _grab.X, Location.Y + point.Y - _grab.Y);
    }

    public void PlaceBottomCentre()
    {
        Screen screen = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Rectangle area = screen.WorkingArea;
        Location = new Point(area.Left + ((area.Width - Width) / 2), area.Bottom - Height - 40);
    }

    /// <summary>Keeps the bar out of every screenshot the recorder takes.</summary>
    public void HideFromCaptures(bool hide)
    {
        NativeMethods.ExcludeFromCapture(Handle, hide);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (GraphicsPath path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12))
        using (var back = new SolidBrush(Color.FromArgb(20, 21, 25)))
        using (var edge = new Pen(Color.FromArgb(64, 68, 78)))
        {
            graphics.FillPath(back, path);
            graphics.DrawPath(edge, path);
        }

        Color dot = _paused ? Color.FromArgb(240, 180, 60) : Color.FromArgb(226, 68, 68);
        using (var halo = new SolidBrush(Color.FromArgb(60, dot)))
        {
            graphics.FillEllipse(halo, 16, 20, 16, 16);
        }

        using (var brush = new SolidBrush(dot))
        {
            graphics.FillEllipse(brush, 20, 24, 8, 8);
        }
    }

    /// <summary>Called on a timer rather than during painting so the label cannot loop.</summary>
    public void UpdateStatus()
    {
        TimeSpan elapsed = ElapsedSource?.Invoke() ?? TimeSpan.Zero;
        int steps = StepCountSource?.Invoke() ?? 0;
        string label = _paused ? "Paused" : elapsed.ToString(@"mm\:ss");
        string text = $"{label}   {steps} {(steps == 1 ? "step" : "steps")}";

        if (!string.Equals(_status.Text, text, StringComparison.Ordinal))
        {
            _status.Text = text;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _tick.Stop();
        _tick.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;

            // Tool window keeps the bar out of the task switcher.
            parameters.ExStyle |= 0x00000080;
            return parameters;
        }
    }
}
