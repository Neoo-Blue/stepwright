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
///
/// Hook callbacks run on the user interface thread and do exactly one thing: take a single
/// screen grab, which has to happen before the clicked application redraws itself. Everything
/// slow, the accessibility lookup and the picture encoding, happens on the worker behind the
/// queue. When a click ends a burst of typing, both steps share that one grab rather than
/// paying for a second copy of the same screen inside the same callback.
/// </summary>
public sealed class Recorder : IDisposable
{
    private const int DragThreshold = 14;
    private const int DoubleClickSlop = 6;
    private const int RepeatSuppressionMilliseconds = 350;

    private readonly AppSettings _settings;
    private readonly InputHook _hook = new();
    private readonly System.Windows.Forms.Timer _typingTimer = new();
    private readonly List<Regex> _redactors = new();
    private readonly Stopwatch _clock = new();

    private BlockingCollection<object>? _queue;
    private Thread? _worker;
    private string _mediaFolder = string.Empty;
    private string _sessionStamp = string.Empty;
    private int _imageCounter;
    private int _stepCount;

    // Mouse gesture state, touched only on the user interface thread.
    // Times come from the monotonic tick count, because a clock adjustment must not
    // turn every click into a double click.
    private Point _downPoint;
    private long _lastLeftDownTick;
    private Point _lastLeftDownPoint;
    private bool _downWasRecorded;
    private long _lastScrollTick;
    private int _lastScrollSign;
    private bool _lastScrollHorizontal;
    private string _lastCommandKey = string.Empty;
    private long _lastCommandTick;

    // Typing state, touched only on the user interface thread.
    private readonly StringBuilder _typing = new();
    private Task<ElementInfo?>? _typingTargetLookup;

    // Worker state.
    private Step? _lastStep;
    private ElementInfo? _lastElement;
    private Point _lastClickScreenPoint;
    private string _previousApp = string.Empty;

    public Recorder(AppSettings settings)
    {
        _settings = settings;
        _hook.MouseAction += OnMouse;
        _hook.KeyAction += OnKey;

        _typingTimer.Tick += (_, _) => FlushTyping(null);
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

        _sessionStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
        _imageCounter = 0;
        _stepCount = 0;
        _lastStep = null;
        _lastElement = null;
        _previousApp = string.Empty;
        ResetGestureState();
        UiInspector.ResetCache();
        BuildRedactors();

        var queue = new BlockingCollection<object>(new ConcurrentQueue<object>());
        _queue = queue;

        // The hook goes in first. If Windows refuses it there is no worker to tidy up.
        _hook.Install();

        _worker = new Thread(() => WorkerLoop(queue))
        {
            IsBackground = true,
            Name = "Stepwright recorder worker",
        };

        _worker.Start();

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

        FlushTyping(null);
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

        ResetGestureState();
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

        FlushTyping(null);
        _typingTimer.Stop();
        _hook.Uninstall();
        _hook.Suspended = false;
        _clock.Stop();

        BlockingCollection<object>? queue = _queue;
        Thread? worker = _worker;

        // Closing the queue lets the worker finish everything already captured and then end
        // on its own. Nothing in band can be lost or consumed by the wrong reader.
        queue?.CompleteAdding();

        // A short wait, because this runs on the window thread. Anything still in the queue
        // when the wait runs out keeps arriving through the usual event afterwards, so the
        // editor simply fills in the last steps a moment after it opens.
        bool finished = worker is null || !worker.IsAlive || worker.Join(TimeSpan.FromSeconds(5));

        State = RecorderState.Idle;

        if (finished)
        {
            _worker = null;
            _queue = null;
            DrainQueue(queue);
            queue?.Dispose();
        }

        // When the worker overran its deadline the queue and the thread are deliberately
        // left alone, because disposing either one underneath a live consumer would crash.
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Captures the screen as a step of its own, with no click marker.</summary>
    public void CaptureManualShot()
    {
        if (State != RecorderState.Recording)
        {
            return;
        }

        Point cursor = ScreenCapture.CursorPosition();
        CapturedFrame frame = ScreenCapture.Grab(cursor, _settings.CaptureAllMonitors);
        FlushTyping(frame);
        Enqueue(new ShotWork { Frame = frame, Point = cursor });
    }

    private void ResetGestureState()
    {
        _lastLeftDownTick = 0;
        _lastLeftDownPoint = Point.Empty;
        _lastScrollTick = 0;
        _lastScrollSign = 0;
        _lastScrollHorizontal = false;
        _lastCommandKey = string.Empty;
        _lastCommandTick = 0;
        _downWasRecorded = false;
        _typing.Clear();
        _typingTargetLookup = null;
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
                // The timeout stops a pattern that backtracks badly from wedging the worker.
                _redactors.Add(new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(200)));
            }
            catch
            {
                // An invalid expression is ignored rather than breaking the recording.
            }
        }
    }

    /// <summary>Adds work to the queue, releasing the frame when the queue has already closed.</summary>
    private void Enqueue(object work)
    {
        BlockingCollection<object>? queue = _queue;
        try
        {
            if (queue is { IsAddingCompleted: false })
            {
                queue.Add(work);
                return;
            }
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
            // The recording ended between the check and the add.
        }

        FrameOf(work)?.Release();
    }

    private static CapturedFrame? FrameOf(object work) => work switch
    {
        ClickWork click => click.Frame,
        TypeWork typing => typing.Frame,
        KeyWork key => key.Frame,
        ScrollWork scroll => scroll.Frame,
        ShotWork shot => shot.Frame,
        _ => null,
    };

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
        _downWasRecorded = false;

        if (BelongsToThisApp(e.Location))
        {
            return;
        }

        long now = Environment.TickCount64;

        // Only a left click can arm a double click, and only a left click can complete one.
        bool repeatOfLeft = e.Button == MouseButtonKind.Left
            && _lastLeftDownTick != 0
            && now - _lastLeftDownTick <= NativeMethods.GetDoubleClickTime()
            && Math.Abs(e.Location.X - _lastLeftDownPoint.X) <= DoubleClickSlop
            && Math.Abs(e.Location.Y - _lastLeftDownPoint.Y) <= DoubleClickSlop;

        if (e.Button == MouseButtonKind.Left)
        {
            _lastLeftDownTick = now;
            _lastLeftDownPoint = e.Location;
        }

        StepKind kind = e.Button switch
        {
            MouseButtonKind.Right => StepKind.RightClick,
            MouseButtonKind.Middle => StepKind.MiddleClick,
            _ => StepKind.Click,
        };

        CapturedFrame frame = ScreenCapture.Grab(e.Location, _settings.CaptureAllMonitors);
        FlushTyping(frame);

        // The frame always travels with the click. If the worker decides this really was the
        // second half of a double click it promotes the earlier step and drops this picture,
        // which means no interaction can ever be lost on the way.
        Enqueue(new ClickWork
        {
            Frame = frame,
            Point = e.Location,
            Kind = kind,
            MayBeSecondClick = repeatOfLeft,
        });

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

        Enqueue(new PromoteWork { Kind = StepKind.Drag, Detail = direction });
    }

    private void HandleWheel(MouseInputEventArgs e)
    {
        if (!_settings.CaptureScroll || BelongsToThisApp(e.Location))
        {
            return;
        }

        long now = Environment.TickCount64;
        int sign = Math.Sign(e.WheelDelta);

        bool sameBurst = now - _lastScrollTick < 900
            && sign == _lastScrollSign
            && e.Horizontal == _lastScrollHorizontal;

        _lastScrollTick = now;
        _lastScrollSign = sign;
        _lastScrollHorizontal = e.Horizontal;

        if (sameBurst)
        {
            return;
        }

        CapturedFrame frame = ScreenCapture.Grab(e.Location, _settings.CaptureAllMonitors);
        FlushTyping(frame);
        Enqueue(new ScrollWork
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

        if (KeyNames.IsModifier(e.VirtualKey))
        {
            return;
        }

        // The recorder shortcuts belong to the app, not to the guide. When the shortcuts
        // carry modifiers, the bare key still belongs to whatever is being recorded.
        bool modifiersMatch = !_settings.HotkeyNeedsModifiers || (e.Control && e.Shift);
        if (modifiersMatch
            && (e.VirtualKey == (uint)_settings.HotkeyStartPause
                || e.VirtualKey == (uint)_settings.HotkeyStop
                || e.VirtualKey == (uint)_settings.HotkeyShot))
        {
            return;
        }

        // Keyboard focus has nothing to do with where the pointer happens to rest.
        if (IsForegroundOurs())
        {
            return;
        }

        bool commandCombo = (e.Control ^ e.Alt) || e.Windows;

        if (commandCombo)
        {
            string combo = KeyNames.DescribeCombination(e.Control, e.Alt, e.Shift, e.Windows, e.VirtualKey);
            RecordCommandKey(combo);
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
            string name = e.Shift
                ? KeyNames.DescribeCombination(false, false, true, false, e.VirtualKey)
                : KeyNames.Describe(e.VirtualKey);

            RecordCommandKey(name);
        }
    }

    /// <summary>
    /// Records a named key or a shortcut as its own step, ignoring the stream of repeats
    /// Windows sends while a key is held down.
    /// </summary>
    private void RecordCommandKey(string keys)
    {
        long now = Environment.TickCount64;
        if (string.Equals(keys, _lastCommandKey, StringComparison.Ordinal)
            && now - _lastCommandTick < RepeatSuppressionMilliseconds)
        {
            _lastCommandTick = now;
            return;
        }

        _lastCommandKey = keys;
        _lastCommandTick = now;

        Point cursor = ScreenCapture.CursorPosition();
        CapturedFrame frame = ScreenCapture.Grab(cursor, _settings.CaptureAllMonitors);
        FlushTyping(frame);
        Enqueue(new KeyWork { Frame = frame, Point = cursor, Keys = keys });
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
            FlushTyping(null);
        }
    }

    private void RestartTypingTimer()
    {
        _typingTimer.Stop();
        _typingTimer.Interval = Math.Max(400, _settings.TypingMergeMilliseconds);
        _typingTimer.Start();
    }

    /// <summary>
    /// Turns the buffered keystrokes into a step. When the burst was ended by another
    /// interaction, that interaction's screen grab is shared rather than taking a second one.
    /// </summary>
    private void FlushTyping(CapturedFrame? shared)
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

        CapturedFrame frame;
        if (shared is not null)
        {
            shared.AddReference();
            frame = shared;
        }
        else
        {
            frame = ScreenCapture.Grab(ScreenCapture.CursorPosition(), _settings.CaptureAllMonitors);
        }

        Enqueue(new TypeWork { Frame = frame, Text = text, Target = lookup });
    }

    private void WorkerLoop(BlockingCollection<object> queue)
    {
        try
        {
            foreach (object item in queue.GetConsumingEnumerable())
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
                    }
                }
                catch
                {
                    // One bad step must not end the recording.
                }
                finally
                {
                    // Whatever happened above, the screen grab is handed back here.
                    FrameOf(item)?.Release();
                }
            }
        }
        catch
        {
            // The queue was disposed underneath the reader. Nothing is left to do.
        }
    }

    /// <summary>Throws away anything captured after the recording was told to stop.</summary>
    private static void DrainQueue(BlockingCollection<object>? queue)
    {
        if (queue is null)
        {
            return;
        }

        try
        {
            while (queue.TryTake(out object? leftover))
            {
                FrameOf(leftover)?.Release();
            }
        }
        catch
        {
            // Nothing useful to do if the queue is already gone.
        }
    }

    private string SaveFrame(CapturedFrame frame) =>
        frame.SaveOnce(_mediaFolder, () => $"{_sessionStamp}_{Interlocked.Increment(ref _imageCounter):D4}.png");

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
        if (work.MayBeSecondClick && TryPromoteToDoubleClick(work))
        {
            return;
        }

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

        if (!element.WindowBounds.IsEmpty)
        {
            step.WindowArea = RectI.From(work.Frame.ToImageRect(element.WindowBounds));
        }

        step.Text = StepTextBuilder.Describe(work.Kind, element, place);
        step.OriginalText = step.Text;
        step.Image = SaveFrame(work.Frame);

        _lastClickScreenPoint = work.Point;
        Publish(step, element);
    }

    /// <summary>
    /// Folds a rapid second click into the step the first one produced. Returns false when
    /// the previous step is no longer a plain click, in which case the caller records a
    /// normal step instead of dropping the interaction.
    /// </summary>
    private bool TryPromoteToDoubleClick(ClickWork work)
    {
        if (_lastStep is not { Kind: StepKind.Click } previous || _lastElement is null)
        {
            return false;
        }

        if (Math.Abs(work.Point.X - _lastClickScreenPoint.X) > DoubleClickSlop
            || Math.Abs(work.Point.Y - _lastClickScreenPoint.Y) > DoubleClickSlop)
        {
            return false;
        }

        bool edited = !string.Equals(previous.Text, previous.OriginalText, StringComparison.Ordinal);
        previous.Kind = StepKind.DoubleClick;

        if (!edited)
        {
            previous.Text = StepTextBuilder.Describe(StepKind.DoubleClick, _lastElement, string.Empty);
            previous.OriginalText = previous.Text;
        }

        StepChanged?.Invoke(this, previous);
        return true;
    }

    private void HandleTypeWork(TypeWork work)
    {
        // The focused control was looked up as soon as typing began. Collect it here,
        // where waiting costs nothing, rather than inside the input hook.
        ElementInfo? focused = null;
        try
        {
            if (work.Target is not null && work.Target.Wait(800))
            {
                focused = work.Target.Result;
            }
        }
        catch
        {
            // Treated below as an unknown target.
        }

        ElementInfo element = focused ?? new ElementInfo();
        string place = AppPlace(element);

        // A remote session shows another computer as a picture, so the system reports the
        // viewer's own window and never a password box. Failing closed there would blank every
        // value typed on the far machine, which is most of what a remote guide is made of, so
        // typing is written down and the person's own redaction patterns are what guard it.
        bool remote = element.IsRemoteSession || UiInspector.IsRemoteViewer(ForegroundProcess());

        // Fail closed everywhere else. If the target could not be identified there is no way to
        // know it was not a password box, and the typed characters must not be written down.
        bool unknownTarget = focused is null;
        bool secret = _settings.RedactPasswords && !remote && (element.IsPassword || unknownTarget);
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
            TypedText = shown,
            Redacted = secret,
        };

        if (!element.Bounds.IsEmpty)
        {
            step.ElementArea = RectI.From(work.Frame.ToImageRect(element.Bounds));
        }

        if (!element.WindowBounds.IsEmpty)
        {
            step.WindowArea = RectI.From(work.Frame.ToImageRect(element.WindowBounds));
        }

        if (secret)
        {
            string suffix = string.IsNullOrEmpty(place) ? string.Empty : " in " + place;
            step.Text = element.IsPassword
                ? StepTextBuilder.DescribeRedactedTyping(element, suffix)
                : "Enter your details" + suffix + ".";
        }
        else
        {
            step.Text = StepTextBuilder.Describe(StepKind.Type, element, place, shown);
        }

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

    /// <summary>Which process owns the window in front, or zero when that cannot be told.</summary>
    private static int ForegroundProcess()
    {
        try
        {
            IntPtr window = NativeMethods.GetForegroundWindow();

            if (window == IntPtr.Zero)
            {
                return 0;
            }

            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
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
            catch (RegexMatchTimeoutException)
            {
                // A pattern that cannot finish in time is treated as a match, which is the
                // safe reading: the person asked for this text to be hidden.
                return "hidden";
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
    }

    private sealed class ClickWork
    {
        public required CapturedFrame Frame { get; init; }
        public required Point Point { get; init; }
        public required StepKind Kind { get; init; }

        /// <summary>Close enough in time and place to be the second half of a double click.</summary>
        public required bool MayBeSecondClick { get; init; }
    }

    private sealed class TypeWork
    {
        public required CapturedFrame Frame { get; init; }
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
}
