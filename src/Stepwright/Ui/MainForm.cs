using System.Text;
using Stepwright.Ai;
using Stepwright.Config;
using Stepwright.Export;
using Stepwright.Model;
using Stepwright.Native;
using Stepwright.Recording;
using Stepwright.Render;

namespace Stepwright.Ui;

public sealed class MainForm : Form
{
    private readonly AppSettings _settings;
    private readonly Recorder _recorder;
    private readonly HotkeyManager _hotkeys = new();
    private readonly Dictionary<string, Image> _thumbnails = new(StringComparer.Ordinal);
    private readonly HashSet<string> _thumbnailsInFlight = new(StringComparer.Ordinal);

    private readonly ListBox _list = new();
    private readonly TextBox _title = new();
    private readonly TextBox _summary = new();
    private readonly TextBox _stepText = new();
    private readonly TextBox _stepNotes = new();
    private readonly PreviewCanvas _canvas = new();
    private readonly ToolStripStatusLabel _statusLeft = new();
    private readonly ToolStripStatusLabel _statusRight = new();
    private readonly ToolStripButton _recordButton = new();
    private readonly ToolStripButton _stopButton = new();
    private readonly ToolStripDropDownButton _exportButton = new();
    private readonly ToolStripButton _polishButton = new();
    private readonly ToolStripLabel _toolLabel = new();

    private readonly List<ToolStripButton> _toolButtons = new();
    private readonly List<ToolStripButton> _variantButtons = new();
    private readonly ToolStripButton _animateButton = new();
    private CropVariant _lastVariant = CropVariant.Focus;
    private int _hoverIndex = -1;
    private readonly ToolStripMenuItem _markerToggle = new();
    private readonly ToolStripMenuItem _outlineToggle = new();
    private readonly ToolStripMenuItem _zoomToggle = new();

    private Guide _guide = new();
    private SplitContainer? _split;
    private RecorderBar? _bar;
    private bool _loading;
    private Color _drawColor = Color.FromArgb(255, 59, 48);

    public MainForm(AppSettings settings)
    {
        _settings = settings;
        _recorder = new Recorder(settings);
        _recorder.StepAdded += OnStepAdded;
        _recorder.StepChanged += OnStepChanged;

        _drawColor = StepRenderer.Parse(settings.MarkerColor, Color.OrangeRed);

        Text = "Stepwright";
        MinimumSize = new Size(1040, 680);
        Size = new Size(1320, 860);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;

        try
        {
            // The window icon comes from the executable, so it always matches the shipped build.
            if (Environment.ProcessPath is { } executable)
            {
                Icon = Icon.ExtractAssociatedIcon(executable);
            }
        }
        catch
        {
            // A missing icon is not worth stopping for.
        }

        BuildLayout();
        NewGuide(askToSave: false);

        Load += OnFormLoad;
        FormClosing += OnFormClosing;
        KeyDown += OnFormKeyDown;
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        // The splitter sizes are applied in the load handler, once the control has a real
        // width. A fresh split container is only as wide as its default size, and the
        // limits have to fit inside that width.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            SplitterWidth = 3,
            FixedPanel = FixedPanel.Panel1,
            BackColor = Theme.Border,
        };

        SplitContainer split = _split;

        split.Panel1.Controls.Add(BuildLibraryPanel());
        split.Panel2.Controls.Add(_canvas);
        split.Panel2.Controls.Add(BuildCanvasTools());
        split.Panel2.Controls.Add(BuildStepEditor());

        _canvas.Dock = DockStyle.Fill;
        _canvas.RegionDrawn += OnRegionDrawn;
        _canvas.PointPicked += OnPointPicked;

        Controls.Add(split);
        Controls.Add(BuildStatusBar());
        Controls.Add(BuildMainToolbar());
    }

    private ToolStrip BuildMainToolbar()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = new ThemeRenderer(),
            BackColor = Theme.Panel,
            Padding = new Padding(8, 6, 8, 6),
            ImageScalingSize = new Size(14, 14),
        };

        strip.Items.Add(Button("New", "Start an empty guide", NewGuideClicked));
        strip.Items.Add(Button("Open", "Open a saved guide", OpenClicked));
        strip.Items.Add(Button("Save", "Save this guide", (_, _) => SaveGuide(saveAs: false)));
        strip.Items.Add(new ToolStripSeparator());

        _recordButton.Text = "Record";
        _recordButton.ToolTipText = "Start recording every click and keystroke";
        _recordButton.Tag = "primary";
        _recordButton.Image = Dot(Color.White);
        _recordButton.ImageScaling = ToolStripItemImageScaling.None;
        _recordButton.Click += (_, _) => ToggleRecording();
        strip.Items.Add(_recordButton);

        _stopButton.Text = "Finish";
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopRecording();
        strip.Items.Add(_stopButton);
        strip.Items.Add(new ToolStripSeparator());

        strip.Items.Add(Button("Add note", "Insert a step with text only", (_, _) => InsertStep(StepKind.Note)));
        strip.Items.Add(Button("Add heading", "Start a new section", (_, _) => InsertStep(StepKind.Heading)));
        strip.Items.Add(new ToolStripSeparator());

        _polishButton.Text = "Improve with AI";
        _polishButton.ToolTipText = "Rewrite every step, and add notes, with the assistant";
        _polishButton.Click += async (_, _) => await PolishAsync().ConfigureAwait(true);
        strip.Items.Add(_polishButton);

        _exportButton.Text = "Export";
        _exportButton.ShowDropDownArrow = true;
        _exportButton.DropDown.Renderer = new ThemeRenderer();
        _exportButton.DropDown.BackColor = Theme.Panel;
        _exportButton.DropDownItems.Add(Item("Web page, everything in one file", (_, _) => ExportHtml(embed: true)));
        _exportButton.DropDownItems.Add(Item("Web page with an images folder", (_, _) => ExportHtml(embed: false)));
        _exportButton.DropDownItems.Add(Item("Markdown with an images folder", (_, _) => ExportMarkdown()));
        _exportButton.DropDownItems.Add(Item("Word document", (_, _) => ExportDocx()));
        _exportButton.DropDownItems.Add(Item("PDF", (_, _) => ExportPdf()));
        _exportButton.DropDownItems.Add(Item("The whole guide as one animation", (_, _) => ExportReel()));
        _exportButton.DropDownItems.Add(new ToolStripSeparator());
        _exportButton.DropDownItems.Add(Item("Copy for pasting into a knowledge base", (_, _) => CopyRich()));
        _exportButton.DropDownItems.Add(Item("Copy the HTML source", (_, _) => CopyHtmlSource()));
        _exportButton.DropDownItems.Add(Item("Copy as plain text", (_, _) => CopyPlainText()));
        strip.Items.Add(_exportButton);

        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(Button("Settings", "Change how Stepwright records and looks", (_, _) => OpenSettings()));

        return strip;
    }

    private Control BuildLibraryPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(12, 12, 12, 6),
        };

        // A table rather than fixed positions, so a label can never be clipped and the
        // whole thing follows the font size the display is using.
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Panel,
            Margin = new Padding(0),
            Padding = new Padding(0, 0, 0, 8),
        };

        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _title.Multiline = false;
        _title.BorderStyle = BorderStyle.FixedSingle;
        _title.BackColor = Theme.Raised;
        _title.ForeColor = Theme.Text;
        _title.Font = new Font(Theme.Ui.FontFamily, 10.5f);
        _title.Dock = DockStyle.Fill;
        _title.Margin = new Padding(0, 2, 0, 10);
        _title.TextChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _guide.Title = _title.Text;
            MarkDirty();
        };

        _summary.Multiline = true;
        _summary.Height = 46;
        _summary.BorderStyle = BorderStyle.FixedSingle;
        _summary.BackColor = Theme.Raised;
        _summary.ForeColor = Theme.Text;
        _summary.Dock = DockStyle.Fill;
        _summary.Margin = new Padding(0, 2, 0, 0);
        _summary.TextChanged += (_, _) =>
        {
            if (_loading)
            {
                return;
            }

            _guide.Summary = _summary.Text;
            MarkDirty();
        };

        header.Controls.Add(Caption("Guide title"));
        header.Controls.Add(_title);
        header.Controls.Add(Caption("Summary"));
        header.Controls.Add(_summary);

        _list.Dock = DockStyle.Fill;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.ItemHeight = 78;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Theme.Panel;
        _list.ForeColor = Theme.Text;
        _list.IntegralHeight = false;
        _list.DrawItem += OnDrawStep;
        _list.SelectedIndexChanged += (_, _) => LoadSelectedStep();
        _list.KeyDown += OnListKeyDown;
        _list.MouseMove += OnListMouseMove;
        _list.MouseLeave += (_, _) => SetHover(-1);

        var listTools = new ToolStrip
        {
            Dock = DockStyle.Bottom,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = new ThemeRenderer(),
            BackColor = Theme.Panel,
            Padding = new Padding(0, 4, 0, 4),
            CanOverflow = false,
        };

        listTools.Items.Add(Button("Up", "Move this step earlier", (_, _) => MoveStep(-1)));
        listTools.Items.Add(Button("Down", "Move this step later", (_, _) => MoveStep(1)));
        listTools.Items.Add(Button("Merge", "Fold this step into the one above it", (_, _) => MergeIntoPrevious()));
        listTools.Items.Add(Button("Copy", "Duplicate this step", (_, _) => DuplicateStep()));
        listTools.Items.Add(Button("Hide", "Leave this step out of the export", (_, _) => ToggleSkip()));
        listTools.Items.Add(Button("Delete", "Remove this step", (_, _) => DeleteStep()));

        panel.Controls.Add(_list);
        panel.Controls.Add(listTools);
        panel.Controls.Add(header);
        return panel;
    }

    /// <summary>A small label above a field, sized by its own text so it cannot be cut off.</summary>
    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.UiSmall,
        Margin = new Padding(1, 0, 0, 0),
        BackColor = Color.Transparent,
    };

    private Control BuildStepEditor()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Window,
            Padding = new Padding(14, 10, 14, 10),
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Window,
            Margin = new Padding(0),
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _stepText.Multiline = true;
        _stepText.Height = 52;
        _stepText.BorderStyle = BorderStyle.FixedSingle;
        _stepText.BackColor = Theme.Raised;
        _stepText.ForeColor = Theme.Text;
        _stepText.Font = new Font(Theme.Ui.FontFamily, 10.5f);
        _stepText.Dock = DockStyle.Fill;
        _stepText.Margin = new Padding(0, 2, 0, 10);
        _stepText.TextChanged += OnStepTextChanged;

        _stepNotes.Multiline = true;
        _stepNotes.Height = 40;
        _stepNotes.BorderStyle = BorderStyle.FixedSingle;
        _stepNotes.BackColor = Theme.Raised;
        _stepNotes.ForeColor = Theme.Text;
        _stepNotes.Dock = DockStyle.Fill;
        _stepNotes.Margin = new Padding(0, 2, 0, 0);
        _stepNotes.TextChanged += (_, _) =>
        {
            if (_loading || SelectedStep is not { } step)
            {
                return;
            }

            step.Notes = _stepNotes.Text;
            MarkDirty();
        };

        table.Controls.Add(Caption("Step text"));
        table.Controls.Add(_stepText);
        table.Controls.Add(Caption("Note, shown under the step"));
        table.Controls.Add(_stepNotes);

        panel.Controls.Add(table);
        return panel;
    }

    private ToolStrip BuildCanvasTools()
    {
        var strip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Renderer = new ThemeRenderer(),
            BackColor = Theme.Panel,
            Padding = new Padding(8, 4, 8, 4),
            CanOverflow = false,
        };

        var framing = new ToolStripLabel("Framing") { ForeColor = Theme.Muted };
        strip.Items.Add(framing);

        AddVariant(strip, "Full", CropVariant.Full, "The whole screen as it was captured");
        AddVariant(strip, "Window", CropVariant.Window, "Just the window that was in use");
        AddVariant(strip, "Focus", CropVariant.Focus, "Around the control, with room to see where it sits");
        AddVariant(strip, "Close", CropVariant.Close, "Tight on the control");

        _animateButton.Text = "Animate";
        _animateButton.ToolTipText =
            "Export this step as a short animation that starts wide and settles on the control";
        _animateButton.Click += (_, _) => ToggleAnimation();
        strip.Items.Add(_animateButton);

        strip.Items.Add(new ToolStripSeparator());

        AddTool(strip, "Select", CanvasTool.Select, "Click a callout to remove it");
        AddTool(strip, "Box", CanvasTool.Box, "Draw a box");
        AddTool(strip, "Arrow", CanvasTool.Arrow, "Draw an arrow");
        AddTool(strip, "Mark", CanvasTool.Highlight, "Highlight an area");
        AddTool(strip, "Blur", CanvasTool.Blur, "Hide sensitive information");
        AddTool(strip, "Label", CanvasTool.Text, "Place a text label");
        AddTool(strip, "Crop", CanvasTool.Crop, "Draw your own crop");
        AddTool(strip, "Point", CanvasTool.Marker, "Move the click marker");

        strip.Items.Add(new ToolStripSeparator());

        var colour = new ToolStripButton("Colour")
        {
            ToolTipText = "Colour used for new callouts",
            Image = Dot(_drawColor),
            ImageScaling = ToolStripItemImageScaling.None,
        };

        colour.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = _drawColor, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _drawColor = dialog.Color;
                _canvas.DrawColor = _drawColor;
                SetDot(colour, _drawColor);
            }
        };

        strip.Items.Add(colour);

        var more = new ToolStripDropDownButton("Options")
        {
            ShowDropDownArrow = true,
        };

        more.DropDown.Renderer = new ThemeRenderer();
        more.DropDown.BackColor = Theme.Panel;

        SetUpToggle(_markerToggle, "Show the click marker", (step, on) => step.ShowClickMarker = on);
        SetUpToggle(_outlineToggle, "Outline the control", (step, on) => step.ShowElementOutline = on);
        SetUpToggle(_zoomToggle, "Zoom automatically", (step, on) => step.AutoZoom = on);

        more.DropDownItems.Add(_markerToggle);
        more.DropDownItems.Add(_outlineToggle);
        more.DropDownItems.Add(_zoomToggle);
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add(Item("Clear every callout on this step", (_, _) => ClearAnnotations()));
        more.DropDownItems.Add(Item("Apply this framing to every step", (_, _) => ApplyFramingToAll()));
        more.DropDownItems.Add(new ToolStripSeparator());
        more.DropDownItems.Add(Item("Animate every step that can be", (_, _) => AnimateAll()));
        more.DropDownItems.Add(Item("Preview this animation", (_, _) => PreviewAnimation()));
        more.DropDownItems.Add(Item("Save this animation as a file", (_, _) => SaveAnimation()));

        strip.Items.Add(more);

        strip.Items.Add(new ToolStripSeparator());
        _toolLabel.ForeColor = Theme.Muted;
        _toolLabel.Text = "Select";
        strip.Items.Add(_toolLabel);

        return strip;
    }

    private void AddVariant(ToolStrip strip, string text, CropVariant variant, string tip)
    {
        var button = new ToolStripButton(text) { ToolTipText = tip, Tag = variant };
        button.Click += (_, _) => ApplyVariant(variant);
        _variantButtons.Add(button);
        strip.Items.Add(button);
    }

    /// <summary>Reframes the selected step, and remembers the choice for the next step shown.</summary>
    private void ApplyVariant(CropVariant variant)
    {
        _lastVariant = variant;
        ShowVariantChoice(variant);

        if (SelectedStep is not { } step || !step.HasImage)
        {
            return;
        }

        ApplyVariantTo(step, variant);
        MarkDirty();
        RefreshPreview();
        DropThumbnail(step);
        _list.Invalidate();
    }

    private void ApplyVariantTo(Step step, CropVariant variant)
    {
        if (variant == CropVariant.Focus)
        {
            // Focus stays live, so a later change of padding still applies.
            step.Crop = null;
            step.AutoZoom = true;
        }
        else if (variant == CropVariant.Full)
        {
            step.Crop = null;
            step.AutoZoom = false;
        }
        else
        {
            Size size = SourceSize(step);
            if (size.IsEmpty)
            {
                return;
            }

            step.Crop = RectI.From(StepRenderer.VariantCrop(step, size, variant, _settings.ZoomPadding));
            step.AutoZoom = false;
        }

        _loading = true;
        _zoomToggle.Checked = step.AutoZoom;
        _loading = false;
    }

    private void ApplyFramingToAll()
    {
        if (_guide.Steps.Count == 0)
        {
            return;
        }

        foreach (Step step in _guide.Steps.Where(s => s.HasImage))
        {
            ApplyVariantTo(step, _lastVariant);
            DropThumbnail(step);
        }

        MarkDirty();
        RefreshPreview();
        _list.Invalidate();
        Status($"Every step now uses the {_lastVariant.ToString().ToLowerInvariant()} framing.");
    }

    /// <summary>
    /// Marks a step to be exported as a short animation. There is nothing to set up: the
    /// movement is worked out from where the click landed and which control was used.
    /// </summary>
    private void ToggleAnimation()
    {
        if (SelectedStep is not { } step || !StepAnimator.CanAnimate(step))
        {
            return;
        }

        step.Animate = !step.Animate;
        _animateButton.Checked = step.Animate;
        MarkDirty();
        _list.Invalidate();

        Status(step.Animate
            ? "This step will export as an animation. Web pages and Markdown show it; a document uses the still picture."
            : "This step will export as a still picture.");
    }

    private void AnimateAll()
    {
        int count = 0;
        foreach (Step step in _guide.Steps.Where(StepAnimator.CanAnimate))
        {
            step.Animate = true;
            count++;
        }

        if (count == 0)
        {
            Status("None of these steps has a click to move towards.");
            return;
        }

        MarkDirty();
        LoadSelectedStep();
        _list.Invalidate();
        Status($"{count} steps will export as animations.");
    }

    private async void PreviewAnimation()
    {
        byte[]? animation = await BuildAnimationAsync().ConfigureAwait(true);
        if (animation is null)
        {
            return;
        }

        try
        {
            // Written beside the working files and opened with whatever shows pictures,
            // because the editor itself deliberately keeps showing the still frame.
            string path = Path.Combine(Path.GetTempPath(), "stepwright-preview.gif");
            File.WriteAllBytes(path, animation);
            OpenInShell(path);
            Status("Opened the animation in your picture viewer.");
        }
        catch (Exception error)
        {
            Warn("The animation could not be opened. " + error.Message);
        }
    }

    private async void SaveAnimation()
    {
        byte[]? animation = await BuildAnimationAsync().ConfigureAwait(true);
        if (animation is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Animation (*.gif)|*.gif",
            FileName = GuideStore.SuggestFileName(_guide) + " step.gif",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            File.WriteAllBytes(dialog.FileName, animation);
            Status("Saved to " + dialog.FileName);
        }
        catch (Exception error)
        {
            Warn("The animation could not be saved. " + error.Message);
        }
    }

    private async Task<byte[]?> BuildAnimationAsync()
    {
        if (SelectedStep is not { } step)
        {
            return null;
        }

        if (!StepAnimator.CanAnimate(step))
        {
            Warn("This step has no click to move towards, so there is nothing to animate.");
            return null;
        }

        Cursor = Cursors.WaitCursor;
        Status("Building the animation...");

        try
        {
            Guide guide = _guide;
            AppSettings settings = _settings;

            byte[]? animation = await Task
                .Run(() => GuideRenderer.RenderAnimation(guide, step, settings))
                .ConfigureAwait(true);

            if (animation is null)
            {
                Warn("The animation could not be built for this step.");
            }

            return animation;
        }
        catch (Exception error)
        {
            Warn("The animation could not be built. " + error.Message);
            return null;
        }
        finally
        {
            Cursor = Cursors.Default;
            Status("Ready.");
        }
    }

    /// <summary>Reads the size of the untouched screenshot behind a step.</summary>
    private Size SourceSize(Step step)
    {
        string path = _guide.ImagePath(step);
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return Size.Empty;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using Image image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            return image.Size;
        }
        catch
        {
            return Size.Empty;
        }
    }

    private void ShowVariantChoice(CropVariant? variant)
    {
        foreach (ToolStripButton button in _variantButtons)
        {
            button.Checked = variant is not null && (CropVariant)button.Tag! == variant;
        }
    }

    private StatusStrip BuildStatusBar()
    {
        var strip = new StatusStrip
        {
            Dock = DockStyle.Bottom,
            BackColor = Theme.Panel,
            Renderer = new ThemeRenderer(),
            SizingGrip = false,
        };

        _statusLeft.Spring = true;
        _statusLeft.TextAlign = ContentAlignment.MiddleLeft;
        _statusLeft.ForeColor = Theme.Muted;
        _statusRight.ForeColor = Theme.Muted;

        strip.Items.Add(_statusLeft);
        strip.Items.Add(_statusRight);
        return strip;
    }

    private void AddTool(ToolStrip strip, string text, CanvasTool tool, string tip)
    {
        var button = new ToolStripButton(text)
        {
            ToolTipText = tip,
            CheckOnClick = false,
            Tag = tool,
        };

        button.Click += (_, _) =>
        {
            _canvas.Tool = tool;
            _canvas.DrawColor = _drawColor;
            _toolLabel.Text = text;
            foreach (ToolStripButton other in _toolButtons)
            {
                other.Checked = ReferenceEquals(other, button);
            }
        };

        button.Checked = tool == CanvasTool.Select;
        _toolButtons.Add(button);
        strip.Items.Add(button);
    }

    private void SetUpToggle(ToolStripMenuItem button, string text, Action<Step, bool> apply)
    {
        button.Text = text;
        button.CheckOnClick = true;
        button.Click += (_, _) =>
        {
            if (_loading || SelectedStep is not { } step)
            {
                return;
            }

            apply(step, button.Checked);
            MarkDirty();
            RefreshPreview();
        };
    }

    private static ToolStripButton Button(string text, string tip, EventHandler handler)
    {
        var button = new ToolStripButton(text) { ToolTipText = tip };
        button.Click += handler;
        return button;
    }

    private static ToolStripMenuItem Item(string text, EventHandler handler)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += handler;
        return item;
    }

    private static void SetDot(ToolStripItem item, Color color)
    {
        Image? previous = item.Image;
        item.Image = Dot(color);
        previous?.Dispose();
    }

    private static Bitmap Dot(Color color)
    {
        var bitmap = new Bitmap(14, 14);
        using Graphics graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 2, 2, 10, 10);
        return bitmap;
    }

    // ---------------------------------------------------------------- guide handling

    private Step? SelectedStep => _list.SelectedIndex >= 0 && _list.SelectedIndex < _guide.Steps.Count
        ? _guide.Steps[_list.SelectedIndex]
        : null;

    private void NewGuideClicked(object? sender, EventArgs e) => NewGuide(askToSave: true);

    private void NewGuide(bool askToSave)
    {
        if (askToSave && !ConfirmDiscard())
        {
            return;
        }

        _guide = new Guide
        {
            Title = "Untitled guide",
            Author = _settings.Author,
            MediaFolder = Path.Combine(GuideStore.CreateWorkFolder(), "media"),
        };

        Directory.CreateDirectory(_guide.MediaFolder);
        ClearThumbnails();
        RebuildList();
        LoadGuideFields();
        Status("Press Record, or the shortcut, and Stepwright writes the steps for you.");
    }

    private void LoadGuideFields()
    {
        _loading = true;
        _title.Text = _guide.Title;
        _summary.Text = _guide.Summary;
        _loading = false;
        UpdateCaption();
    }

    private void OpenClicked(object? sender, EventArgs e)
    {
        if (!ConfirmDiscard())
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = GuideStore.FileFilter,
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _guide = GuideStore.Load(dialog.FileName);
            ClearThumbnails();
            RebuildList();
            LoadGuideFields();
            Status("Opened " + Path.GetFileName(dialog.FileName));
        }
        catch (Exception error)
        {
            Warn("That guide could not be opened. " + error.Message);
        }
    }

    private bool SaveGuide(bool saveAs)
    {
        string? path = _guide.FilePath;

        if (saveAs || string.IsNullOrEmpty(path))
        {
            using var dialog = new SaveFileDialog
            {
                Filter = GuideStore.FileFilter,
                FileName = GuideStore.SuggestFileName(_guide) + GuideStore.FileExtension,
                InitialDirectory = EnsureLibraryFolder(),
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return false;
            }

            path = dialog.FileName;
        }

        try
        {
            GuideStore.Save(_guide, path);
            UpdateCaption();
            Status("Saved to " + path);
            return true;
        }
        catch (Exception error)
        {
            Warn("The guide could not be saved. " + error.Message);
            return false;
        }
    }

    private string EnsureLibraryFolder()
    {
        try
        {
            Directory.CreateDirectory(_settings.LibraryFolder);
            return _settings.LibraryFolder;
        }
        catch
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    private bool ConfirmDiscard()
    {
        if (!_guide.Dirty)
        {
            return true;
        }

        DialogResult answer = MessageBox.Show(
            this,
            "This guide has changes that are not saved. Save it now?",
            "Stepwright",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);

        return answer switch
        {
            DialogResult.Yes => SaveGuide(saveAs: false),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void MarkDirty()
    {
        _guide.Dirty = true;
        UpdateCaption();
    }

    private void UpdateCaption()
    {
        string name = _guide.FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(_guide.FilePath);
        Text = $"Stepwright   {name}{(_guide.Dirty ? " (unsaved)" : string.Empty)}";
        int steps = _guide.Visible.Count(s => s.Kind != StepKind.Heading);
        int hidden = _guide.Steps.Count(s => s.Skip);
        _statusRight.Text = (steps == 1 ? "1 step" : steps + " steps")
            + (hidden > 0 ? $"   {hidden} hidden" : string.Empty);
    }

    // ---------------------------------------------------------------- list

    private void RebuildList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (Step step in _guide.Steps)
        {
            _list.Items.Add(step);
        }

        _list.EndUpdate();

        if (_list.Items.Count > 0 && _list.SelectedIndex < 0)
        {
            _list.SelectedIndex = 0;
        }
        else if (_list.Items.Count == 0)
        {
            _canvas.SetImage(null, Point.Empty);
            _loading = true;
            _stepText.Text = string.Empty;
            _stepNotes.Text = string.Empty;
            _loading = false;
        }

        UpdateCaption();
    }

    private void OnDrawStep(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count)
        {
            return;
        }

        var step = (Step)_list.Items[e.Index];
        Graphics graphics = e.Graphics;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        bool hovered = e.Index == _hoverIndex && !selected;

        using (var back = new SolidBrush(Theme.Panel))
        {
            graphics.FillRectangle(back, e.Bounds);
        }

        var card = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + 3, e.Bounds.Width - 14, e.Bounds.Height - 6);

        if (selected)
        {
            Theme.FillRounded(graphics, card, Theme.AccentSoft);
            Theme.DrawRounded(graphics, card, Theme.Accent);
        }
        else if (hovered)
        {
            Theme.FillRounded(graphics, card, Theme.Raised);
        }

        if (step.Kind == StepKind.Heading)
        {
            using var headingInk = new SolidBrush(step.Skip ? Theme.Muted : Theme.Text);
            graphics.DrawString(
                string.IsNullOrWhiteSpace(step.Text) ? "Section" : step.Text,
                Theme.UiTitle,
                headingInk,
                new RectangleF(card.X + 12, card.Y + 22, card.Width - 24, 28));

            using var line = new Pen(Theme.Border);
            graphics.DrawLine(line, card.X + 12, card.Bottom - 8, card.Right - 12, card.Bottom - 8);
            return;
        }

        // Thumbnail first, because it anchors the row visually.
        var thumbArea = new Rectangle(card.X + 10, card.Y + 8, 84, card.Height - 16);
        Image? thumbnail = Thumbnail(step);

        if (thumbnail is not null)
        {
            Rectangle fitted = Fit(thumbnail.Size, thumbArea);
            using (var clip = Theme.RoundedRect(fitted, 4))
            {
                System.Drawing.Drawing2D.GraphicsState state = graphics.Save();
                graphics.SetClip(clip);
                graphics.DrawImage(thumbnail, fitted);
                graphics.Restore(state);
            }

            Theme.DrawRounded(graphics, fitted, Theme.Border, 4);
        }
        else
        {
            Theme.FillRounded(graphics, thumbArea, Theme.Raised, 4);
        }

        int number = NumberOf(e.Index);
        var badge = new Rectangle(thumbArea.X - 4, thumbArea.Y - 4, 20, 20);

        using (var badgeBrush = new SolidBrush(step.Skip ? Theme.Border : Theme.Accent))
        {
            graphics.FillEllipse(badgeBrush, badge);
        }

        using (var badgeInk = new SolidBrush(Color.White))
        {
            graphics.DrawString(number.ToString(), Theme.UiTiny, badgeInk, badge, CentredText);
        }

        int textLeft = thumbArea.Right + 12;
        int textWidth = card.Right - textLeft - 10;

        using var ink = new SolidBrush(step.Skip ? Theme.Muted : Theme.Text);
        graphics.DrawString(
            step.Text,
            Theme.UiStep,
            ink,
            new RectangleF(textLeft, card.Y + 9, textWidth, card.Height - 30),
            WrappedText);

        // A quiet line at the foot of the card saying what kind of action this was.
        string kind = Describe(step.Kind);
        string trailer = step.Skip ? "hidden" : step.Animate ? kind + "   animated" : kind;

        using var trailerInk = new SolidBrush(Theme.Muted);
        graphics.DrawString(
            trailer,
            Theme.UiSmall,
            trailerInk,
            new RectangleF(textLeft, card.Bottom - 20, textWidth, 16));
    }

    private static string Describe(StepKind kind) => kind switch
    {
        StepKind.Click => "Click",
        StepKind.DoubleClick => "Double click",
        StepKind.RightClick => "Right click",
        StepKind.MiddleClick => "Middle click",
        StepKind.Drag => "Drag",
        StepKind.Type => "Typing",
        StepKind.Hotkey => "Shortcut",
        StepKind.Scroll => "Scroll",
        StepKind.Screenshot => "Screen",
        StepKind.Note => "Note",
        _ => string.Empty,
    };

    // Held rather than rebuilt, because the list redraws these on every repaint.
    private static readonly StringFormat CentredText = new()
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
    };

    private static readonly StringFormat WrappedText = new()
    {
        Trimming = StringTrimming.EllipsisWord,
    };

    private static Rectangle Fit(Size image, Rectangle area)
    {
        double scale = Math.Min(area.Width / (double)image.Width, area.Height / (double)image.Height);
        int width = Math.Max(1, (int)(image.Width * scale));
        int height = Math.Max(1, (int)(image.Height * scale));
        return new Rectangle(area.X + ((area.Width - width) / 2), area.Y + ((area.Height - height) / 2), width, height);
    }

    private int NumberOf(int index)
    {
        int number = 0;
        for (int i = 0; i <= index && i < _guide.Steps.Count; i++)
        {
            if (_guide.Steps[i].Kind != StepKind.Heading)
            {
                number++;
            }
        }

        return number;
    }

    private Image? Thumbnail(Step step)
    {
        if (!step.HasImage)
        {
            return null;
        }

        if (_thumbnails.TryGetValue(step.Id, out Image? cached))
        {
            return cached;
        }

        if (!_thumbnailsInFlight.Add(step.Id))
        {
            return null;
        }

        string path = _guide.ImagePath(step);
        string id = step.Id;

        Task.Run(() =>
        {
            Image? built = null;
            bool missing = false;
            try
            {
                if (File.Exists(path))
                {
                    using Bitmap source = Stepwright.Capture.ScreenCapture.LoadUnlocked(path);
                    built = StepRenderer.Resize(source, 172, 116);
                }
                else
                {
                    missing = true;
                }
            }
            catch
            {
                built = null;
            }

            try
            {
                BeginInvoke(() =>
                {
                    // A file that is not there stays marked, so a repaint does not retry it
                    // forever. Anything else was a passing failure and is worth another go.
                    if (!missing)
                    {
                        _thumbnailsInFlight.Remove(id);
                    }

                    if (built is not null)
                    {
                        _thumbnails[id] = built;
                        _list.Invalidate();
                    }
                });
            }
            catch
            {
                built?.Dispose();
            }
        });

        return null;
    }

    private void ClearThumbnails()
    {
        foreach (Image image in _thumbnails.Values)
        {
            image.Dispose();
        }

        _thumbnails.Clear();
        _thumbnailsInFlight.Clear();
    }

    private void DropThumbnail(Step step)
    {
        _thumbnailsInFlight.Remove(step.Id);
        if (_thumbnails.Remove(step.Id, out Image? image))
        {
            image.Dispose();
        }
    }

    // ---------------------------------------------------------------- step editing

    private void LoadSelectedStep()
    {
        Step? step = SelectedStep;
        _loading = true;

        _stepText.Text = step?.Text ?? string.Empty;
        _stepNotes.Text = step?.Notes ?? string.Empty;
        _markerToggle.Checked = step?.ShowClickMarker ?? false;
        _outlineToggle.Checked = step?.ShowElementOutline ?? false;
        _zoomToggle.Checked = step?.AutoZoom ?? false;

        bool hasImage = step?.HasImage ?? false;
        _markerToggle.Enabled = hasImage;
        _outlineToggle.Enabled = hasImage;
        _zoomToggle.Enabled = hasImage;

        foreach (ToolStripButton button in _variantButtons)
        {
            button.Enabled = hasImage;
        }

        bool canAnimate = step is not null && StepAnimator.CanAnimate(step);
        _animateButton.Enabled = canAnimate;
        _animateButton.Checked = canAnimate && step!.Animate;

        ShowVariantChoice(step is null ? null : CurrentVariant(step));

        _loading = false;
        RefreshPreview();
    }

    /// <summary>Works out which of the ready made framings a step is currently using.</summary>
    private static CropVariant? CurrentVariant(Step step)
    {
        if (step.Crop is null)
        {
            return step.AutoZoom ? CropVariant.Focus : CropVariant.Full;
        }

        return null;
    }

    private void OnStepTextChanged(object? sender, EventArgs e)
    {
        if (_loading || SelectedStep is not { } step)
        {
            return;
        }

        step.Text = _stepText.Text;
        MarkDirty();

        int index = _list.SelectedIndex;
        if (index >= 0)
        {
            _list.Invalidate(_list.GetItemRectangle(index));
        }
    }

    private void RefreshPreview()
    {
        Step? step = SelectedStep;
        if (step is null || !step.HasImage)
        {
            bool empty = step is null;
            _canvas.EmptyMessage = empty ? "Nothing recorded yet" : "This step has no screenshot";
            _canvas.EmptyHint = empty
                ? $"Press Record, or F{_settings.HotkeyStartPause - 0x6F} from anywhere, then do the task once."
                : "Add a note, or a heading, to give the reader some context.";
            _canvas.SetImage(null, Point.Empty);
            return;
        }

        RenderedStep? rendered = GuideRenderer.RenderDetailed(_guide, step, _settings, 2400);
        if (rendered is null)
        {
            _canvas.EmptyMessage = "The screenshot is missing";
            _canvas.EmptyHint = "The picture file for this step could not be read.";
            _canvas.SetImage(null, Point.Empty);
            return;
        }

        _canvas.SetImage(rendered.Image, rendered.Origin, rendered.Scale);
    }

    private void OnRegionDrawn(object? sender, RegionEventArgs e)
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        switch (e.Tool)
        {
            case CanvasTool.Crop:
                step.Crop = RectI.From(e.Region);
                step.AutoZoom = false;
                _loading = true;
                _zoomToggle.Checked = false;
                _loading = false;
                break;

            case CanvasTool.Blur:
                step.Annotations.Add(new Annotation { Kind = AnnotationKind.Blur, Area = RectI.From(e.Region) });
                break;

            case CanvasTool.Box:
                step.Annotations.Add(new Annotation
                {
                    Kind = AnnotationKind.Rectangle,
                    Area = RectI.From(e.Region),
                    Color = StepRenderer.ToHex(_drawColor),
                });
                break;

            case CanvasTool.Highlight:
                step.Annotations.Add(new Annotation
                {
                    Kind = AnnotationKind.Highlight,
                    Area = RectI.From(e.Region),
                    Color = StepRenderer.ToHex(_drawColor),
                });
                break;

            case CanvasTool.Arrow:
                step.Annotations.Add(new Annotation
                {
                    Kind = AnnotationKind.Arrow,
                    Area = RectI.From(e.Region),
                    Color = StepRenderer.ToHex(_drawColor),
                });
                break;

            default:
                return;
        }

        MarkDirty();
        RefreshPreview();
    }

    private void OnPointPicked(object? sender, PointEventArgs e)
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        switch (e.Tool)
        {
            case CanvasTool.Marker:
                step.ClickPoint = PointI.From(e.Location);
                step.ShowClickMarker = true;
                _loading = true;
                _markerToggle.Checked = true;
                _loading = false;
                break;

            case CanvasTool.Text:
            {
                string label = Prompt("Label text", "Add a label");
                if (string.IsNullOrWhiteSpace(label))
                {
                    return;
                }

                var annotation = new Annotation
                {
                    Kind = AnnotationKind.Text,
                    Text = label,
                    Color = StepRenderer.ToHex(_drawColor),
                };

                Size pill = StepRenderer.MeasureLabel(label, annotation.Thickness);
                annotation.Area = new RectI(e.Location.X, e.Location.Y, pill.Width, pill.Height);
                step.Annotations.Add(annotation);
                break;
            }

            case CanvasTool.Select:
            {
                Annotation? hit = step.Annotations
                    .LastOrDefault(a => Inflate(a.Area.Rect).Contains(e.Location));

                if (hit is null)
                {
                    return;
                }

                step.Annotations.Remove(hit);
                break;
            }

            default:
                return;
        }

        MarkDirty();
        RefreshPreview();
    }

    private static Rectangle Inflate(Rectangle rect)
    {
        var normal = new Rectangle(
            Math.Min(rect.X, rect.X + rect.Width),
            Math.Min(rect.Y, rect.Y + rect.Height),
            Math.Abs(rect.Width),
            Math.Abs(rect.Height));

        return Rectangle.Inflate(normal, 12, 12);
    }

    private void ClearAnnotations()
    {
        if (SelectedStep is not { } step || step.Annotations.Count == 0)
        {
            return;
        }

        step.Annotations.Clear();
        MarkDirty();
        RefreshPreview();
    }

    private void ResetCrop()
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        step.Crop = null;
        MarkDirty();
        RefreshPreview();
    }

    private void InsertStep(StepKind kind)
    {
        var step = new Step
        {
            Kind = kind,
            Text = kind == StepKind.Heading ? "New section" : "Add your own note here.",
            ShowClickMarker = false,
            AutoZoom = false,
        };

        step.OriginalText = step.Text;

        int index = _list.SelectedIndex < 0 ? _guide.Steps.Count : _list.SelectedIndex + 1;
        _guide.Steps.Insert(index, step);
        RebuildList();
        _list.SelectedIndex = index;
        _stepText.Focus();
        _stepText.SelectAll();
        MarkDirty();
    }

    private void MoveStep(int direction)
    {
        int index = _list.SelectedIndex;
        int target = index + direction;
        if (index < 0 || target < 0 || target >= _guide.Steps.Count)
        {
            return;
        }

        (_guide.Steps[index], _guide.Steps[target]) = (_guide.Steps[target], _guide.Steps[index]);
        RebuildList();
        _list.SelectedIndex = target;
        MarkDirty();
    }

    private void DuplicateStep()
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        Step copy = step.Clone();
        int index = _list.SelectedIndex + 1;
        _guide.Steps.Insert(index, copy);
        RebuildList();
        _list.SelectedIndex = index;
        MarkDirty();
    }

    /// <summary>
    /// Folds a step into the one before it. The earlier screenshot is kept, because it shows
    /// the state the reader is looking at when they start the combined instruction.
    /// </summary>
    private void MergeIntoPrevious()
    {
        int index = _list.SelectedIndex;
        if (index < 1)
        {
            return;
        }

        Step current = _guide.Steps[index];
        Step previous = _guide.Steps[index - 1];

        if (previous.Kind == StepKind.Heading || current.Kind == StepKind.Heading)
        {
            Status("A section heading cannot be merged.");
            return;
        }

        string tail = current.Text.Trim();
        if (tail.Length > 0)
        {
            string head = previous.Text.TrimEnd();
            if (head.EndsWith('.'))
            {
                head = head[..^1];
            }

            tail = char.ToLowerInvariant(tail[0]) + tail[1..];
            previous.Text = head + ", then " + tail;
        }

        if (!string.IsNullOrWhiteSpace(current.Notes))
        {
            previous.Notes = string.IsNullOrWhiteSpace(previous.Notes)
                ? current.Notes
                : previous.Notes + " " + current.Notes;
        }

        if (!previous.HasImage && current.HasImage)
        {
            previous.Image = current.Image;
            previous.ClickPoint = current.ClickPoint;
            previous.ElementArea = current.ElementArea;
            previous.Crop = current.Crop;
            current.Image = string.Empty;
        }

        DropThumbnail(current);
        _guide.Steps.RemoveAt(index);
        RebuildList();
        _list.SelectedIndex = index - 1;
        MarkDirty();
    }

    private void ToggleSkip()
    {
        if (SelectedStep is not { } step)
        {
            return;
        }

        step.Skip = !step.Skip;
        _list.Invalidate();
        MarkDirty();
    }

    private void DeleteStep()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _guide.Steps.Count)
        {
            return;
        }

        Step step = _guide.Steps[index];
        DropThumbnail(step);
        _guide.Steps.RemoveAt(index);
        RebuildList();

        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = Math.Min(index, _list.Items.Count - 1);
        }

        MarkDirty();
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.S)
        {
            SaveGuide(saveAs: e.Shift);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.O)
        {
            OpenClicked(this, EventArgs.Empty);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        int index = _list.IndexFromPoint(e.Location);
        SetHover(index == ListBox.NoMatches ? -1 : index);
    }

    private void SetHover(int index)
    {
        if (_hoverIndex == index)
        {
            return;
        }

        int previous = _hoverIndex;
        _hoverIndex = index;

        if (previous >= 0 && previous < _list.Items.Count)
        {
            _list.Invalidate(_list.GetItemRectangle(previous));
        }

        if (index >= 0 && index < _list.Items.Count)
        {
            _list.Invalidate(_list.GetItemRectangle(index));
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete)
        {
            DeleteStep();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.Up)
        {
            MoveStep(-1);
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.Down)
        {
            MoveStep(1);
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- recording

    private void ToggleRecording()
    {
        switch (_recorder.State)
        {
            case RecorderState.Recording:
                _recorder.Pause();
                if (_bar is not null)
                {
                    _bar.Paused = true;
                }

                Status("Recording paused.");
                break;

            case RecorderState.Paused:
                _recorder.Resume();
                if (_bar is not null)
                {
                    _bar.Paused = false;
                }

                Status("Recording again.");
                break;

            default:
                StartRecording();
                break;
        }
    }

    private void StartRecording()
    {
        if (string.IsNullOrEmpty(_guide.MediaFolder))
        {
            _guide.MediaFolder = Path.Combine(GuideStore.CreateWorkFolder(), "media");
        }

        Directory.CreateDirectory(_guide.MediaFolder);

        _recordButton.Text = "Pause";
        _recordButton.Tag = null;
        SetDot(_recordButton, Color.FromArgb(240, 180, 60));
        _stopButton.Enabled = true;

        WindowState = FormWindowState.Minimized;

        if (_settings.CountdownSeconds > 0)
        {
            var countdown = new CountdownForm(_settings.CountdownSeconds);
            countdown.FormClosed += (_, _) => BeginRecordingNow();
            countdown.Show();
        }
        else
        {
            BeginRecordingNow();
        }
    }

    private void BeginRecordingNow()
    {
        try
        {
            _recorder.Start(_guide.MediaFolder);
        }
        catch (Exception error)
        {
            WindowState = FormWindowState.Normal;
            ResetRecordButtons();
            Warn("Recording could not start. " + error.Message);
            return;
        }

        _bar = new RecorderBar
        {
            ElapsedSource = () => _recorder.Elapsed,
            StepCountSource = () => _guide.Steps.Count(s => s.Kind != StepKind.Heading),
        };

        _bar.PauseRequested += (_, _) => ToggleRecording();
        _bar.StopRequested += (_, _) => StopRecording();
        _bar.ShotRequested += (_, _) => _recorder.CaptureManualShot();
        _bar.PlaceBottomCentre();
        _bar.Show();

        if (_settings.HideAppFromCaptures)
        {
            _bar.HideFromCaptures(true);
            NativeMethods.ExcludeFromCapture(Handle, true);
        }

        RegisterRecordingHotkeys();
        Status("Recording. Everything you click and type becomes a step.");
    }

    private void StopRecording()
    {
        if (_recorder.State == RecorderState.Idle)
        {
            return;
        }

        _recorder.Stop();

        if (_bar is not null)
        {
            _bar.Close();
            _bar.Dispose();
            _bar = null;
        }

        NativeMethods.ExcludeFromCapture(Handle, false);
        ResetRecordButtons();

        WindowState = FormWindowState.Normal;
        Activate();

        RebuildList();
        if (_list.Items.Count > 0)
        {
            _list.SelectedIndex = 0;
        }

        MarkDirty();
        Status($"Recording finished with {_guide.Steps.Count} steps. Tidy the wording, then export.");
    }

    private void ResetRecordButtons()
    {
        _recordButton.Text = "Record";
        _recordButton.Tag = "primary";
        SetDot(_recordButton, Color.White);
        _stopButton.Enabled = false;
    }

    private void OnStepAdded(object? sender, Step step)
    {
        // Steps arrive on the recorder worker thread.
        if (!MarshalToUi(() => OnStepAdded(sender, step)))
        {
            return;
        }

        if (_settings.AddHeadingOnAppChange && !string.IsNullOrWhiteSpace(step.AppName))
        {
            Step? previous = _guide.Steps.LastOrDefault(s => s.Kind != StepKind.Heading);
            bool changed = previous is not null
                && !string.Equals(previous.AppName, step.AppName, StringComparison.OrdinalIgnoreCase);

            if (previous is null || changed)
            {
                var heading = new Step
                {
                    Kind = StepKind.Heading,
                    Text = step.AppName,
                    ShowClickMarker = false,
                    AutoZoom = false,
                };

                heading.OriginalText = heading.Text;
                _guide.Steps.Add(heading);
                _list.Items.Add(heading);
            }
        }

        _guide.Steps.Add(step);
        _list.Items.Add(step);
        _guide.Dirty = true;

        if (_bar is not null)
        {
            _bar.UpdateStatus();
        }

        UpdateCaption();
    }

    private void OnStepChanged(object? sender, Step step)
    {
        if (!MarshalToUi(() => OnStepChanged(sender, step)))
        {
            return;
        }

        int index = _guide.Steps.IndexOf(step);
        if (index >= 0 && index < _list.Items.Count)
        {
            _list.Invalidate(_list.GetItemRectangle(index));
        }

        if (ReferenceEquals(step, SelectedStep))
        {
            LoadSelectedStep();
        }
    }

    /// <summary>
    /// Returns true when the caller is already on the user interface thread and may continue.
    /// Otherwise the work is posted there and the caller stops. Posting rather than waiting
    /// matters, because the recorder joins its worker while this thread is inside Stop.
    /// </summary>
    private bool MarshalToUi(Action work)
    {
        if (!InvokeRequired)
        {
            return true;
        }

        try
        {
            if (IsHandleCreated && !IsDisposed)
            {
                BeginInvoke(work);
            }
        }
        catch (ObjectDisposedException)
        {
            // The window closed while a step was still being written.
        }
        catch (InvalidOperationException)
        {
            // The handle went away between the check and the call.
        }

        return false;
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        uint modifiers = _settings.HotkeyNeedsModifiers
            ? NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT
            : 0;

        bool ok = _hotkeys.Register(modifiers, (uint)_settings.HotkeyStartPause, ToggleRecording);
        ok &= _hotkeys.Register(modifiers, (uint)_settings.HotkeyStop, StopRecording);
        ok &= _hotkeys.Register(modifiers, (uint)_settings.HotkeyShot, () => _recorder.CaptureManualShot());

        if (!ok)
        {
            Status("Some shortcuts are already in use by another program. Change them in Settings.");
        }
    }

    private void RegisterRecordingHotkeys() => RegisterHotkeys();

    // ---------------------------------------------------------------- export

    private void ExportHtml(bool embed)
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Web page (*.html)|*.html",
            FileName = GuideStore.SuggestFileName(_guide) + ".html",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RunExport(() =>
        {
            HtmlExporter.Export(_guide, _settings, dialog.FileName, new HtmlOptions
            {
                EmbedImages = embed,
                UseJpeg = false,
            });

            return dialog.FileName;
        });
    }

    private void ExportMarkdown()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Markdown (*.md)|*.md",
            FileName = GuideStore.SuggestFileName(_guide) + ".md",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RunExport(() =>
        {
            MarkdownExporter.Export(_guide, _settings, dialog.FileName);
            return dialog.FileName;
        });
    }

    private void ExportDocx()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "Word document (*.docx)|*.docx",
            FileName = GuideStore.SuggestFileName(_guide) + ".docx",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RunExport(() =>
        {
            DocxExporter.Export(_guide, _settings, dialog.FileName);
            return dialog.FileName;
        });
    }

    private void ExportPdf()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "PDF document (*.pdf)|*.pdf",
            FileName = GuideStore.SuggestFileName(_guide) + ".pdf",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RunExport(() =>
        {
            PdfExporter.Export(_guide, _settings, dialog.FileName);
            return dialog.FileName;
        });
    }

    /// <summary>
    /// The whole guide as a single animation: every step in order, held long enough to read.
    /// Different from the per step animation, which zooms in on one action.
    /// </summary>
    private void ExportReel()
    {
        if (!_guide.Visible.Any(s => s.HasImage))
        {
            Warn("There are no screenshots to animate yet.");
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "Animation (*.gif)|*.gif",
            FileName = GuideStore.SuggestFileName(_guide) + ".gif",
            InitialDirectory = EnsureLibraryFolder(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        Guide guide = _guide;
        AppSettings settings = _settings;
        string path = dialog.FileName;

        RunExport(() =>
        {
            byte[]? reel = GuideAnimator.Build(guide, settings, null, Math.Max(640, settings.GifWidth));
            if (reel is null)
            {
                throw new InvalidOperationException("There was nothing to animate.");
            }

            File.WriteAllBytes(path, reel);
            return path;
        });
    }

    private async void RunExport(Func<string> work)
    {
        Cursor = Cursors.WaitCursor;
        _exportButton.Enabled = false;
        Status("Building the export...");

        try
        {
            // Rendering happens off the window thread so a long guide cannot freeze the app.
            string path = await Task.Run(work).ConfigureAwait(true);
            Status("Exported to " + path);

            if (MessageBox.Show(
                    this,
                    "The export is ready. Open it now?",
                    "Stepwright",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                OpenInShell(path);
            }
        }
        catch (Exception error)
        {
            Warn("The export failed. " + error.Message);
        }
        finally
        {
            _exportButton.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private static void OpenInShell(string path)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = path;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // Nothing sensible to do when no program is associated with the file.
        }
    }

    private void CopyRich()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            string fragment = HtmlExporter.Build(_guide, _settings, new HtmlOptions
            {
                Fragment = true,
                EmbedImages = true,
                UseJpeg = true,
                MaxImageWidth = 1100,
            });

            var data = new DataObject();
            data.SetData(DataFormats.Html, ClipboardHtml.Wrap(fragment));
            data.SetData(DataFormats.UnicodeText, MarkdownExporter.BuildPlain(_guide));
            Clipboard.SetDataObject(data, copy: true);
            Status("Copied. Paste it into your knowledge base or an email.");
        }
        catch (Exception error)
        {
            Warn("The guide could not be copied. " + error.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void CopyHtmlSource()
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            string fragment = HtmlExporter.Build(_guide, _settings, new HtmlOptions
            {
                Fragment = true,
                EmbedImages = true,
                UseJpeg = true,
                MaxImageWidth = 1100,
            });

            Clipboard.SetText(fragment);
            Status("The HTML source is on the clipboard.");
        }
        catch (Exception error)
        {
            Warn("The guide could not be copied. " + error.Message);
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private void CopyPlainText()
    {
        try
        {
            Clipboard.SetText(MarkdownExporter.BuildPlain(_guide));
            Status("The steps are on the clipboard as plain text.");
        }
        catch (Exception error)
        {
            Warn("The steps could not be copied. " + error.Message);
        }
    }

    // ---------------------------------------------------------------- assistant

    private async Task PolishAsync()
    {
        if (!_settings.AiEnabled)
        {
            Warn("Turn the assistant on in Settings first, then choose a service and paste a key.");
            OpenSettings();
            return;
        }

        if (!_settings.HasAiKey && !_settings.AiBaseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            Warn("The assistant needs a key. Add one in Settings.");
            OpenSettings();
            return;
        }

        if (_guide.Steps.Count == 0)
        {
            return;
        }

        _polishButton.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            var progress = new Progress<string>(Status);
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(20));

            // The pictures are prepared together on a worker, so the window stays responsive
            // and the assistant is never waiting on the drawing code.
            var pictures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (_settings.AiSendScreenshots)
            {
                Status("Preparing the screenshots...");
                Guide guide = _guide;
                AppSettings settings = _settings;

                pictures = await Task.Run(() =>
                {
                    var built = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    foreach (Step step in guide.Steps.Where(s => s.HasImage))
                    {
                        byte[]? picture = GuideRenderer.RenderJpeg(guide, step, settings, 1100, 72);
                        if (picture is not null)
                        {
                            built[step.Id] = picture;
                        }
                    }

                    return built;
                }).ConfigureAwait(true);
            }

            int changed = await AiPolisher
                .ImproveAsync(
                    _guide,
                    _settings,
                    step => pictures.TryGetValue(step.Id, out byte[]? picture) ? picture : null,
                    progress,
                    cancel.Token)
                .ConfigureAwait(true);

            if (string.IsNullOrWhiteSpace(_guide.Title) || _guide.Title == "Untitled guide")
            {
                (string title, string summary) = await AiPolisher
                    .SuggestHeadingAsync(_guide, _settings, cancel.Token)
                    .ConfigureAwait(true);

                if (!string.IsNullOrWhiteSpace(title))
                {
                    _guide.Title = title;
                    _guide.Summary = string.IsNullOrWhiteSpace(_guide.Summary) ? summary : _guide.Summary;
                    LoadGuideFields();
                }
            }

            _list.Invalidate();
            LoadSelectedStep();
            MarkDirty();
            Status(changed == 0
                ? "The assistant left every step as it was."
                : $"The assistant rewrote {changed} steps.");
        }
        catch (Exception error)
        {
            Warn("The writing assistant could not finish. " + error.Message);
        }
        finally
        {
            _polishButton.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        RegisterHotkeys();
        RefreshPreview();
        Status("Settings saved.");
    }

    // ---------------------------------------------------------------- plumbing

    private void OnFormLoad(object? sender, EventArgs e)
    {
        Theme.StyleWindow(Handle);
        Theme.Apply(this);

        if (_split is not null && _split.Width > 760)
        {
            _split.Panel1MinSize = 260;
            _split.Panel2MinSize = 420;
            _split.SplitterDistance = 380;
        }
        RegisterHotkeys();
        GuideStore.CleanupOldWorkFolders(TimeSpan.FromDays(7));

        string start = _settings.HotkeyNeedsModifiers ? "Ctrl and Shift and " : string.Empty;
        Status($"Ready. Press {start}F{_settings.HotkeyStartPause - 0x6F} anywhere to start or pause a recording.");
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        if (_recorder.State != RecorderState.Idle)
        {
            StopRecording();
        }

        _hotkeys.Dispose();
        _recorder.Dispose();
        ClearThumbnails();
    }

    private void Status(string message) => _statusLeft.Text = message;

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Stepwright", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private string Prompt(string label, string caption)
    {
        using var dialog = new Form
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 130),
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.Window,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
        };

        var text = new Label { Text = label, Bounds = new Rectangle(16, 14, 320, 18), ForeColor = Theme.Muted };
        var input = new TextBox { Bounds = new Rectangle(16, 36, 328, 26), BackColor = Theme.Raised, ForeColor = Theme.Text };
        var ok = new Button { Text = "Add", Bounds = new Rectangle(184, 78, 76, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(268, 78, 76, 30), DialogResult = DialogResult.Cancel };
        Theme.StyleButton(ok, primary: true);
        Theme.StyleButton(cancel);

        dialog.Controls.Add(text);
        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(this) == DialogResult.OK ? input.Text.Trim() : string.Empty;
    }
}

/// <summary>Wraps a fragment in the shape the Windows clipboard expects for rich paste.</summary>
internal static class ClipboardHtml
{
    public static string Wrap(string fragment)
    {
        const string header =
            "Version:0.9\r\nStartHTML:{0:0000000000}\r\nEndHTML:{1:0000000000}\r\n"
            + "StartFragment:{2:0000000000}\r\nEndFragment:{3:0000000000}\r\n";

        const string before = "<html><body>\r\n<!--StartFragment-->";
        const string after = "<!--EndFragment-->\r\n</body></html>";

        string sample = string.Format(System.Globalization.CultureInfo.InvariantCulture, header, 0, 0, 0, 0);
        int headerLength = Encoding.UTF8.GetByteCount(sample);

        int startHtml = headerLength;
        int startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        int endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        int endHtml = endFragment + Encoding.UTF8.GetByteCount(after);

        string realHeader = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            header,
            startHtml,
            endHtml,
            startFragment,
            endFragment);

        return realHeader + before + fragment + after;
    }
}
