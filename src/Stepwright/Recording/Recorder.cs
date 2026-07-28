using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Stepwright.Capture;
using Stepwright.Config;
using Stepwright.Model;
using Stepwright.Native;

namespace Stepwright.Recording;

public enum RecorderState
{
    Idle,
    Recording,
    Paused,
}

/// <summary>
/// Watches the whole desktop and turns what the person does into finished steps.
/// Hook callbacks stay on the user interface thread and only take a screen grab.
/// Everything slow happens on the worker thread behind the queue.
/// </summary>
public sealed class Recorder : IDisposable
{
    private const int DragThreshold = 14;

    private readonly AppSettings _settings;
    private readonly InputHook _hook = new();
    private readonly BlockingCollection<object> _queue = new(new ConcurrentQueue<object>());
    private readonly System.Windows.Forms.Timer _typingTimer = new();
    private readonly List<Regex> _redactors = new();
    private readonly Stopwatch _clock = new();

    private Thread? _worker;
    private string _mediaFolder = string.Empty;
    private string _sessionStamp = string.Empty;
    private int _imageCounter;
    private int _stepCount;

    // Mouse gesture state, touched only on the user interface thread.
    private Point _downPoint;
    private DateTime _downMoment;
    private DateTime _lastClickMoment = DateTime.MinValue;
    private Point _lastClickPoint;
    private bool _downWasRecorded;
    private DateTime _lastScrollMoment = DateTime.MinValue;
    private int _lastScrollSign;

    // Typing state, touched only on the user interface thread.
    private readonly StringBuilder _typing = new();
    private Task<ElementInfo?>? _typingTargetLookup;

    // Worker state.
    private Step? _lastStep;
    private ElementInfo? _lastElement;
    private string _previousApp = string.Empty;

    public Recorder(AppSettings settings)
    {
        _settings = settings;
        _hook.MouseAction += OnMouse;
        _hook.KeyAction += OnKey;

        _typingTimer.Tick += (_, _) => FlushTyping();
        BuildRedactors();
    }

    public RecorderState State { get; private set; } = RecorderState.Idle;

    public int StepCount => _stepCount;

    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Raised on the worker thread once a step is finished. Subscribers must marshal.</summary>
    public event EventHandler<Step>? StepAdded;

    /// <summary>Raised on the worker thread when an earlier step is rewritten. Subscribers must marshal.</summary>
    public event EventHandler<Step>? StepChanged;

    public event EventHandler? StateChanged;

    public void Start(string mediaFolder)
    {
        if (State != RecorderState.Idle)
        {
            return;
        }

        _mediaFolder = mediaFolder;
        Directory.CreateDirectory(_mediaFolder);

        // A stamp per session keeps file names unique when a recording is added to an existing guide.
        _sessionStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        _imageCounter = 0;
        _stepCount = 0;
        _lastStep = null;
        _lastElement = null;
        _previousApp = string.Empty;
        UiInspector.ResetCache();
        BuildRedactors();

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Stepwright recorder worker",
        };
        _worker.Start();

        _hook.Install();
        _typingTimer.Interval = Math.Max(400, _settings.TypingMergeMilliseconds);

        _clock.Restart();
        State = RecorderState.Recording;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        FlushTyping();
        _hook.Suspended = true;
        _clock.Stop();
        State = RecorderState.Paused;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (State != RecorderState.Paused)
        {
            return;
        }

        _hook.Suspended = false;
        _clock.Start();
        State = RecorderState.Recording;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (State == RecorderState.Idle)
        {
            return;
        }

        FlushTyping();
        _typingTimer.Stop();
        _hook.Uninstall();
        _hook.Suspended = false;
        _clock.Stop();

        // Let the worker finish everything already captured before the editor opens.
        _queue.Add(new StopMarker());
        if (_worker is not null && _worker.IsAlive)
        {
            _worker.Join(TimeSpan.FromSeconds(8));
        }

        _worker = null;
        DrainQueue();
        State = RecorderState.Idle;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Captures the screen as a step of its own, with no click marker.</summary>
    public void CaptureManualShot()
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        FlushTyping();
        Point cursor = ScreenCapture.CursorPosition();
        CapturedFrame frame = ScreenCapture.Grab(cursor, _settings.CaptureAllMonitors);
        _queue.Add(new ShotWork { Frame = frame, Point = cursor });
    }

    private void BuildRedactors()
    {
        _redactors.Clear();
        foreach (string pattern in _settings.RedactPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                _redactors.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            catch
            {
                // An invalid expression is ignored rather than breaking the recording.
            }
        }
    }

    private static bool BelongsToThisApp(Point screenPoint)
    {
        try
        {
            var point = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
            IntPtr hwnd = NativeMethods.WindowFromPoint(point);
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private void OnMouse(object? sender, MouseInputEventArgs e)
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        if (e.WheelDelta != 0)
        {
            HandleWheel(e);
            return;
        }

        if (e.IsDown)
        {
            HandleMouseDown(e);
        }
        else
        {
            HandleMouseUp(e);
        }
    }

    private void HandleMouseDown(MouseInputEventArgs e)
    {
        _downPoint = e.Location;
        _downMoment = e.Moment;
        _downWasRecorded = false;

        if (BelongsToThisApp(e.Location))
        {
            return;
        }

        FlushTyping();

        bool isDouble = e.Button == MouseButtonKind.Left
            && (e.Moment - _lastClickMoment).TotalMilliseconds <= NativeMethods.GetDoubleClickTime()
            && Math.Abs(e.Location.X - _lastClickPoint.X) <= 6
            && Math.Abs(e.Location.Y - _lastClickPoint.Y) <= 6;

        _lastClickMoment = e.Moment;
        _lastClickPoint = e.Location;

        if (isDouble)
        {
            // The first click already produced a step. Promote it instead of adding a second one.
            _queue.Add(new PromoteWork { Kind = StepKind.DoubleClick });
            _downWasRecorded = false;
            return;
        }

        CapturedFrame frame = ScreenCapture.Grab(e.Location, _settings.CaptureAllMonitors);
        StepKind kind = e.Button switch
        {
            MouseButtonKind.Right => StepKind.RightClick,
            MouseButtonKind.Middle => StepKind.MiddleClick,
            _ => StepKind.Click,
        };

        _queue.Add(new ClickWork { Frame = frame, Point = e.Location, Kind = kind });
        _downWasRecorded = true;
    }

    private void HandleMouseUp(MouseInputEventArgs e)
    {
        if (!_settings.CaptureDrag || !_downWasRecorded || e.Button != MouseButtonKind.Left)
        {
            return;
        }

        int dx = e.Location.X - _downPoint.X;
        int dy = e.Location.Y - _downPoint.Y;
        if (Math.Abs(dx) < DragThreshold && Math.Abs(dy) < DragThreshold)
        {
            return;
        }

        string direction = Math.Abs(dx) > Math.Abs(dy)
            ? (dx > 0 ? "to the right" : "to the left")
            : (dy > 0 ? "downwards" : "upwards");

        _queue.Add(new PromoteWork { Kind = StepKind.Drag, Detail = direction });
    }

    private void HandleWheel(MouseInputEventArgs e)
    {
        if (!_settings.CaptureScroll)
        {
            return;
        }

        if (BelongsToThisApp(e.Location))
        {
            return;
        }

        int sign = Math.Sign(e.WheelDelta);
        bool sameBurst = (e.Moment - _lastScrollMoment).TotalMilliseconds < 900 && sign == _lastScrollSign;
        _lastScrollMoment = e.Moment;
        _lastScrollSign = sign;

        if (sameBurst)
        {
            return;
        }

        FlushTyping();
        CapturedFrame frame = ScreenCapture.Grab(e.Location, _settings.CaptureAllMonitors);
        _queue.Add(new ScrollWork
        {
            Frame = frame,
            Point = e.Location,
            Direction = StepTextBuilder.DescribeScrollDirection(e.WheelDelta, e.Horizontal),
        });
    }

    private void OnKey(object? sender, KeyInputEventArgs e)
    {
        if (State != RecorderState.Recording || !_settings.CaptureKeyboard)
        {
            return;
        }

        // The recorder shortcuts belong to the app, not to the guide.
        if (e.VirtualKey == (uint)_settings.HotkeyStartPause
            || e.VirtualKey == (uint)_settings.HotkeyStop
            || e.VirtualKey == (uint)_settings.HotkeyShot)
        {
            return;
        }

        if (KeyNames.IsModifier(e.VirtualKey))
        {
            return;
        }

        if (BelongsToThisApp(ScreenCapture.CursorPosition()) && IsForegroundOurs())
        {
            return;
        }

        bool commandCombo = (e.Control ^ e.Alt) || e.Windows;

        if (commandCombo)
        {
            FlushTyping();
            string combo = KeyNames.DescribeCombination(e.Control, e.Alt, e.Shift, e.Windows, e.VirtualKey);
            CapturedFrame frame = ScreenCapture.Grab(ScreenCapture.CursorPosition(), _settings.CaptureAllMonitors);
            _queue.Add(new KeyWork { Frame = frame, Point = ScreenCapture.CursorPosition(), Keys = combo });
            return;
        }

        if (e.Text is { Length: > 0 })
        {
            AppendTyping(e.Text);
            return;
        }

        if (e.VirtualKey == 0x08 && _typing.Length > 0)
        {
            // Backspace inside a burst simply corrects what was typed.
            _typing.Length--;
            RestartTypingTimer();
            return;
        }

        if (KeyNames.IsNotableCommandKey(e.VirtualKey))
        {
            FlushTyping();

            // Shift and Tab together is a different instruction from Tab on its own.
            string name = e.Shift
                ? KeyNames.DescribeCombination(false, false, true, false, e.VirtualKey)
                : KeyNames.Describe(e.VirtualKey);
            Point cursor = ScreenCapture.CursorPosition();
            CapturedFrame frame = ScreenCapture.Grab(cursor, _settings.CaptureAllMonitors);
            _queue.Add(new KeyWork { Frame = frame, Point = cursor, Keys = name });
        }
    }

    private static bool IsForegroundOurs()
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out uint pid);
            return pid == (uint)Environment.ProcessId;
        }
        catch
        {
            return false;
        }
    }

    private void AppendTyping(string text)
    {
        if (_typing.Length == 0)
        {
            // Work out what is being typed into while the burst is still fresh.
            // The result is collected later on the worker, never on this thread.
            _typingTargetLookup = Task.Run(() => UiInspector.ResolveFocused());
        }

        _typing.Append(text);
        RestartTypingTimer();

        if (_typing.Length >= 120)
        {
            FlushTyping();
        }
    }

    private void RestartTypingTimer()
    {
        _typingTimer.Stop();
        _typingTimer.Interval = Math.Max(400, _settings.TypingMergeMilliseconds);
        _typingTimer.Start();
    }

    private void FlushTyping()
    {
        _typingTimer.Stop();
        if (_typing.Length == 0)
        {
            return;
        }

        string text = _typing.ToString();
        _typing.Clear();

        Task<ElementInfo?>? lookup = _typingTargetLookup;
        _typingTargetLookup = null;

        Point cursor = ScreenCapture.CursorPosition();
        CapturedFrame frame = ScreenCapture.Grab(cursor, _settings.CaptureAllMonitors);
        _queue.Add(new TypeWork { Frame = frame, Point = cursor, Text = text, Target = lookup });
    }

    private void WorkerLoop()
    {
        foreach (object item in _queue.GetConsumingEnumerable())
        {
            try
            {
                switch (item)
                {
                    case ClickWork click:
                        HandleClickWork(click);
                        break;
                    case TypeWork typing:
                        HandleTypeWork(typing);
                        break;
                    case KeyWork key:
                        HandleKeyWork(key);
                        break;
                    case ScrollWork scroll:
                        HandleScrollWork(scroll);
                        break;
                    case ShotWork shot:
                        HandleShotWork(shot);
                        break;
                    case PromoteWork promote:
                        HandlePromoteWork(promote);
                        break;
                    case StopMarker:
                        return;
                }
            }
            catch
            {
                // One bad step must not end the recording.
            }
        }
    }

    /// <summary>Throws away anything captured after the stop marker so the next session starts clean.</summary>
    private void DrainQueue()
    {
        while (_queue.TryTake(out object? leftover))
        {
            switch (leftover)
            {
                case ClickWork click:
                    click.Frame.Dispose();
                    break;
                case TypeWork typing:
                    typing.Frame.Dispose();
                    break;
                case KeyWork key:
                    key.Frame.Dispose();
                    break;
                case ScrollWork scroll:
                    scroll.Frame.Dispose();
                    break;
                case ShotWork shot:
                    shot.Frame.Dispose();
                    break;
            }
        }
    }

    private string SaveFrame(CapturedFrame frame)
    {
        int index = Interlocked.Increment(ref _imageCounter);
        string name = $"{_sessionStamp}_{index:D4}.png";
        try
        {
            ScreenCapture.SavePng(frame.Image, Path.Combine(_mediaFolder, name));
            return name;
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            frame.Dispose();
        }
    }

    private string AppPlace(ElementInfo element)
    {
        string app = StepTextBuilder.AppContext(element);
        if (string.IsNullOrEmpty(app) || string.Equals(app, _previousApp, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        _previousApp = app;
        return app;
    }

    private void HandleClickWork(ClickWork work)
    {
        ElementInfo element = UiInspector.Resolve(work.Point);
        string place = AppPlace(element);

        var step = new Step
        {
            Kind = work.Kind,
            Moment = DateTime.Now,
            ClickPoint = PointI.From(work.Frame.ToImagePoint(work.Point)),
            ShowClickMarker = _settings.ShowClickMarker,
            ShowElementOutline = _settings.ShowElementOutline,
            AutoZoom = _settings.AutoZoom,
            AppName = element.AppName,
            WindowTitle = element.WindowTitle,
            ElementName = element.Name,
            ElementType = element.ControlType,
        };

        if (!element.Bounds.IsEmpty)
        {
            step.ElementArea = RectI.From(work.Frame.ToImageRect(element.Bounds));
        }

        step.Text = StepTextBuilder.Describe(work.Kind, element, place);
        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        Publish(step, element);
    }

    private void HandleTypeWork(TypeWork work)
    {
        // The focused control was looked up as soon as typing began. Collect it here,
        // where waiting costs nothing, rather than inside the input hook.
        ElementInfo? focused = null;
        try
        {
            if (work.Target is not null && work.Target.Wait(600))
            {
                focused = work.Target.Result;
            }
        }
        catch
        {
            // Fall back to a fresh lookup below.
        }

        ElementInfo element = focused is not null && (focused.HasName || focused.IsPassword)
            ? focused
            : UiInspector.Resolve(work.Point);

        string place = AppPlace(element);
        bool secret = _settings.RedactPasswords && element.IsPassword;
        string shown = secret ? string.Empty : Redact(work.Text);

        var step = new Step
        {
            Kind = StepKind.Type,
            Moment = DateTime.Now,
            ShowClickMarker = false,
            ShowElementOutline = _settings.ShowElementOutline,
            AutoZoom = _settings.AutoZoom,
            AppName = element.AppName,
            WindowTitle = element.WindowTitle,
            ElementName = element.Name,
            ElementType = element.ControlType,
            TypedText = secret ? string.Empty : shown,
            Redacted = secret,
        };

        if (!element.Bounds.IsEmpty)
        {
            step.ElementArea = RectI.From(work.Frame.ToImageRect(element.Bounds));
        }

        step.Text = secret
            ? StepTextBuilder.DescribeRedactedTyping(element, string.IsNullOrEmpty(place) ? string.Empty : " in " + place)
            : StepTextBuilder.Describe(StepKind.Type, element, place, shown);
        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        Publish(step, element);
    }

    private void HandleKeyWork(KeyWork work)
    {
        ElementInfo element = UiInspector.Resolve(work.Point, 400);
        string place = AppPlace(element);

        var step = new Step
        {
            Kind = StepKind.Hotkey,
            Moment = DateTime.Now,
            ShowClickMarker = false,
            ShowElementOutline = false,
            AutoZoom = false,
            Keys = work.Keys,
            AppName = element.AppName,
            WindowTitle = element.WindowTitle,
        };

        step.Text = StepTextBuilder.Describe(StepKind.Hotkey, element, place, work.Keys);
        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        Publish(step, element);
    }

    private void HandleScrollWork(ScrollWork work)
    {
        ElementInfo element = UiInspector.Resolve(work.Point, 400);
        string place = AppPlace(element);

        var step = new Step
        {
            Kind = StepKind.Scroll,
            Moment = DateTime.Now,
            ShowClickMarker = false,
            ShowElementOutline = false,
            AutoZoom = false,
            AppName = element.AppName,
            WindowTitle = element.WindowTitle,
        };

        step.Text = StepTextBuilder.Describe(StepKind.Scroll, element, place, work.Direction);
        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        Publish(step, element);
    }

    private void HandleShotWork(ShotWork work)
    {
        ElementInfo element = UiInspector.Resolve(work.Point, 400);

        var step = new Step
        {
            Kind = StepKind.Screenshot,
            Moment = DateTime.Now,
            ShowClickMarker = false,
            ShowElementOutline = false,
            AutoZoom = false,
            AppName = element.AppName,
            WindowTitle = element.WindowTitle,
            Text = StepTextBuilder.Describe(StepKind.Screenshot, element, string.Empty),
        };

        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        Publish(step, element);
    }

    private void HandlePromoteWork(PromoteWork work)
    {
        Step? step = _lastStep;
        if (step is null || _lastElement is null)
        {
            return;
        }

        if (work.Kind == StepKind.DoubleClick && step.Kind != StepKind.Click)
        {
            return;
        }

        if (work.Kind == StepKind.Drag && step.Kind is not (StepKind.Click or StepKind.DoubleClick))
        {
            return;
        }

        bool edited = !string.Equals(step.Text, step.OriginalText, StringComparison.Ordinal);
        step.Kind = work.Kind;

        if (!edited)
        {
            step.Text = StepTextBuilder.Describe(work.Kind, _lastElement, string.Empty, work.Detail);
            step.OriginalText = step.Text;
        }

        StepChanged?.Invoke(this, step);
    }

    private void Publish(Step step, ElementInfo element)
    {
        _lastStep = step;
        _lastElement = element;
        Interlocked.Increment(ref _stepCount);
        StepAdded?.Invoke(this, step);
    }

    private string Redact(string text)
    {
        string result = text;
        foreach (Regex regex in _redactors)
        {
            try
            {
                result = regex.Replace(result, "hidden");
            }
            catch
            {
                // Skip a pattern that misbehaves on this input.
            }
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            Stop();
        }
        catch
        {
            // Shutting down should stay quiet.
        }

        _typingTimer.Dispose();
        _hook.Dispose();
        _queue.Dispose();
    }

    private sealed class ClickWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
        public required StepKind Kind { get; init; }
    }

    private sealed class TypeWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
        public required string Text { get; init; }

        /// <summary>The focused control lookup started when the burst began.</summary>
        public required Task<ElementInfo?>? Target { get; init; }
    }

    private sealed class KeyWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
        public required string Keys { get; init; }
    }

    private sealed class ScrollWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
        public required string Direction { get; init; }
    }

    private sealed class ShotWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
    }

    private sealed class PromoteWork
    {
        public required StepKind Kind { get; init; }
        public string Detail { get; init; } = string.Empty;
    }

    private sealed class StopMarker
    {
    }
}
