using Stepwright.Ai;
using Stepwright.Config;
using Stepwright.Render;

namespace Stepwright.Ui;

public sealed class SettingsForm : Form
{
    private readonly AppSettings _settings;

    private readonly TextBox _author = new();
    private readonly NumericUpDown _countdown = new();
    private readonly CheckBox _allMonitors = new();
    private readonly CheckBox _keyboard = new();
    private readonly CheckBox _scroll = new();
    private readonly CheckBox _drag = new();
    private readonly CheckBox _hideApp = new();
    private readonly NumericUpDown _typingMerge = new();
    private readonly CheckBox _redactPasswords = new();
    private readonly TextBox _redactPatterns = new();

    private readonly CheckBox _autoZoom = new();
    private readonly NumericUpDown _padding = new();
    private readonly CheckBox _marker = new();
    private readonly CheckBox _outline = new();
    private readonly Button _markerColor = new();
    private readonly CheckBox _headings = new();
    private readonly CheckBox _darkTheme = new();

    private readonly ComboBox _keyStart = new();
    private readonly ComboBox _keyStop = new();
    private readonly ComboBox _keyShot = new();
    private readonly CheckBox _keyModifiers = new();

    private readonly CheckBox _aiEnabled = new();
    private readonly ComboBox _aiProvider = new();
    private readonly TextBox _aiBaseUrl = new();
    private readonly TextBox _aiModel = new();
    private readonly TextBox _aiKey = new();
    private readonly CheckBox _aiPictures = new();
    private readonly CheckBox _aiNotes = new();
    private readonly Button _aiTest = new();
    private readonly Label _aiResult = new();
    private readonly LinkLabel _aiKeyLink = new();
    private readonly Label _aiHint = new();

    private Color _chosenMarker;
    private bool _keyEdited;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _chosenMarker = StepRenderer.Parse(settings.MarkerColor, Color.OrangeRed);

        Text = "Stepwright settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 700);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Ui;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6),
        };

        tabs.TabPages.Add(BuildRecordingTab());
        tabs.TabPages.Add(BuildLookTab());
        tabs.TabPages.Add(BuildAssistantTab());

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.Panel };
        var save = new Button { Text = "Save", Bounds = new Rectangle(396, 12, 72, 30), Tag = "primary" };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(476, 12, 72, 30) };
        Theme.StyleButton(save, primary: true);
        Theme.StyleButton(cancel);
        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);

        Controls.Add(tabs);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;

        Load += (_, _) =>
        {
            Theme.Apply(this);
            Theme.EnableDarkTitleBar(Handle);
        };
    }

    private TabPage BuildRecordingTab()
    {
        var page = new TabPage("Recording") { BackColor = Theme.Panel, Padding = new Padding(16) };
        int y = 16;

        AddRow(page, "Your name", _author, ref y);
        _author.Text = _settings.Author;

        AddRow(page, "Countdown before recording, in seconds", _countdown, ref y);
        Configure(_countdown, 0, 10, _settings.CountdownSeconds);

        AddRow(page, "Wait before a typing burst becomes a step, in milliseconds", _typingMerge, ref y);
        Configure(_typingMerge, 400, 6000, _settings.TypingMergeMilliseconds, 100);

        AddCheck(page, _allMonitors, "Capture every monitor instead of the one in use", _settings.CaptureAllMonitors, ref y);
        AddCheck(page, _keyboard, "Record what is typed", _settings.CaptureKeyboard, ref y);
        AddCheck(page, _scroll, "Record scrolling", _settings.CaptureScroll, ref y);
        AddCheck(page, _drag, "Record dragging", _settings.CaptureDrag, ref y);
        AddCheck(page, _hideApp, "Keep Stepwright itself out of the screenshots", _settings.HideAppFromCaptures, ref y);
        AddCheck(page, _redactPasswords, "Never store anything typed into a password box", _settings.RedactPasswords, ref y);

        y += 6;
        page.Controls.Add(new Label
        {
            Text = "Hide anything that matches these patterns, one per line",
            Bounds = new Rectangle(16, y, 480, 18),
            ForeColor = Theme.Muted,
        });
        y += 22;

        _redactPatterns.Multiline = true;
        _redactPatterns.ScrollBars = ScrollBars.Vertical;
        _redactPatterns.Bounds = new Rectangle(16, y, 496, 74);
        _redactPatterns.Text = string.Join(Environment.NewLine, _settings.RedactPatterns);
        page.Controls.Add(_redactPatterns);
        y += 84;

        page.Controls.Add(new Label
        {
            Text = "Shortcuts",
            Bounds = new Rectangle(16, y, 200, 20),
            Font = Theme.UiBold,
        });
        y += 26;

        FillKeys(_keyStart, _settings.HotkeyStartPause);
        FillKeys(_keyStop, _settings.HotkeyStop);
        FillKeys(_keyShot, _settings.HotkeyShot);

        AddInline(page, "Start or pause", _keyStart, 16, y);
        AddInline(page, "Finish", _keyStop, 190, y);
        AddInline(page, "Capture now", _keyShot, 364, y);
        y += 52;

        AddCheck(page, _keyModifiers, "Also hold Ctrl and Shift for these shortcuts", _settings.HotkeyNeedsModifiers, ref y);

        return page;
    }

    private TabPage BuildLookTab()
    {
        var page = new TabPage("Look") { BackColor = Theme.Panel, Padding = new Padding(16) };
        int y = 16;

        AddCheck(page, _autoZoom, "Zoom each screenshot to the part that was used", _settings.AutoZoom, ref y);
        AddRow(page, "Space to keep around the zoomed area, in pixels", _padding, ref y);
        Configure(_padding, 80, 900, _settings.ZoomPadding, 20);

        AddCheck(page, _marker, "Mark where the click landed", _settings.ShowClickMarker, ref y);
        AddCheck(page, _outline, "Outline the control that was used", _settings.ShowElementOutline, ref y);
        AddCheck(page, _headings, "Start a new section when the application changes", _settings.AddHeadingOnAppChange, ref y);
        AddCheck(page, _darkTheme, "Dark window colours", _settings.DarkTheme, ref y);

        y += 8;
        page.Controls.Add(new Label
        {
            Text = "Marker colour",
            Bounds = new Rectangle(16, y + 6, 110, 20),
        });

        _markerColor.Bounds = new Rectangle(132, y, 90, 30);
        _markerColor.Text = string.Empty;
        _markerColor.BackColor = _chosenMarker;
        _markerColor.FlatStyle = FlatStyle.Flat;
        _markerColor.Cursor = Cursors.Hand;
        _markerColor.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { Color = _chosenMarker, FullOpen = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _chosenMarker = dialog.Color;
                _markerColor.BackColor = _chosenMarker;
            }
        };
        page.Controls.Add(_markerColor);
        y += 44;

        page.Controls.Add(new Label
        {
            Text = "A change of colours takes effect the next time Stepwright starts.",
            Bounds = new Rectangle(16, y, 480, 20),
            ForeColor = Theme.Muted,
        });

        return page;
    }

    private TabPage BuildAssistantTab()
    {
        var page = new TabPage("Assistant") { BackColor = Theme.Panel, Padding = new Padding(16) };
        int y = 14;

        page.Controls.Add(new Label
        {
            Text = "Optional. The assistant rewrites the wording of every step and can add a short"
                + Environment.NewLine
                + "note where one helps. Nothing is sent anywhere until you turn this on.",
            Bounds = new Rectangle(16, y, 510, 36),
            ForeColor = Theme.Muted,
        });
        y += 44;

        AddCheck(page, _aiEnabled, "Use the assistant", _settings.AiEnabled, ref y);
        y += 4;

        _aiProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (AiProvider provider in AiProviders.All)
        {
            _aiProvider.Items.Add(provider.Label);
        }

        _aiProvider.SelectedIndex = Math.Max(
            0,
            AiProviders.All.ToList().FindIndex(p => p.Id == _settings.AiProvider));

        AddRow(page, "Service", _aiProvider, ref y);
        _aiProvider.Width = 496;
        _aiProvider.SelectedIndexChanged += (_, _) => ApplyProviderPreset();

        AddRow(page, "Address", _aiBaseUrl, ref y);
        _aiBaseUrl.Text = _settings.AiBaseUrl;

        AddRow(page, "Model", _aiModel, ref y);
        _aiModel.Text = _settings.AiModel;

        AddRow(page, "Key, stored encrypted for this Windows account", _aiKey, ref y);
        _aiKey.UseSystemPasswordChar = true;
        _aiKey.Text = _settings.HasAiKey ? new string('*', 24) : string.Empty;

        // Tracked rather than guessed from the value, because a real key may contain anything.
        _aiKey.TextChanged += (_, _) => _keyEdited = true;

        _aiHint.Bounds = new Rectangle(16, y - 14, 350, 18);
        _aiHint.ForeColor = Theme.Muted;
        _aiHint.Font = Theme.UiSmall;
        page.Controls.Add(_aiHint);

        _aiKeyLink.Bounds = new Rectangle(376, y - 14, 136, 18);
        _aiKeyLink.Text = "Where to get a key";
        _aiKeyLink.Font = Theme.UiSmall;
        _aiKeyLink.TextAlign = ContentAlignment.MiddleRight;
        _aiKeyLink.LinkClicked += (_, _) => OpenKeyPage();
        page.Controls.Add(_aiKeyLink);
        y += 12;

        AddCheck(
            page,
            _aiPictures,
            "Let the assistant see each screenshot, which makes the steps far better",
            _settings.AiSendScreenshots,
            ref y);

        page.Controls.Add(new Label
        {
            Text = "With this on, the picture for each step is sent to the service you chose above."
                + Environment.NewLine
                + "It is the only way the assistant can name what is actually on screen.",
            Bounds = new Rectangle(34, y, 480, 34),
            ForeColor = Theme.Muted,
            Font = Theme.UiSmall,
        });
        y += 40;

        AddCheck(page, _aiNotes, "Write a note under a step when it helps", _settings.AiWriteNotes, ref y);
        y += 6;

        _aiTest.Text = "Test the connection";
        _aiTest.Bounds = new Rectangle(16, y, 150, 30);
        Theme.StyleButton(_aiTest);
        _aiTest.Click += async (_, _) => await TestAsync().ConfigureAwait(true);
        page.Controls.Add(_aiTest);

        _aiResult.Bounds = new Rectangle(176, y + 4, 340, 34);
        _aiResult.ForeColor = Theme.Muted;
        page.Controls.Add(_aiResult);

        ShowProviderHint();
        return page;
    }

    private AiProvider SelectedProvider =>
        AiProviders.All[Math.Clamp(_aiProvider.SelectedIndex, 0, AiProviders.All.Count - 1)];

    /// <summary>Fills the address and the model with values that work for the chosen service.</summary>
    private void ApplyProviderPreset()
    {
        AiProvider provider = SelectedProvider;
        _aiBaseUrl.Text = provider.BaseUrl;
        _aiModel.Text = provider.Model;
        ShowProviderHint();
    }

    private void ShowProviderHint()
    {
        AiProvider provider = SelectedProvider;
        _aiHint.Text = provider.Hint;
        _aiKeyLink.Visible = !string.IsNullOrEmpty(provider.KeyPage);
    }

    private void OpenKeyPage()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = SelectedProvider.KeyPage;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // No browser association, nothing sensible to do.
        }
    }

    private async Task TestAsync()
    {
        _aiTest.Enabled = false;
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Talking to the endpoint...";

        var probe = new AppSettings
        {
            AiProvider = SelectedProvider.Id,
            AiBaseUrl = _aiBaseUrl.Text.Trim(),
            AiModel = _aiModel.Text.Trim(),
            AiKeyProtected = _settings.AiKeyProtected,
        };

        if (_keyEdited && !string.IsNullOrWhiteSpace(_aiKey.Text))
        {
            probe.SetAiKey(_aiKey.Text.Trim());
        }

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string reply = await AiPolisher.TestAsync(probe, cancel.Token).ConfigureAwait(true);
            _aiResult.ForeColor = Theme.Good;
            _aiResult.Text = "Connected. The model said: " + StepwrightText.Shorten(reply, 60);
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 120);
        }
        finally
        {
            _aiTest.Enabled = true;
        }
    }

    private void Commit()
    {
        _settings.Author = _author.Text.Trim();
        _settings.CountdownSeconds = (int)_countdown.Value;
        _settings.TypingMergeMilliseconds = (int)_typingMerge.Value;
        _settings.CaptureAllMonitors = _allMonitors.Checked;
        _settings.CaptureKeyboard = _keyboard.Checked;
        _settings.CaptureScroll = _scroll.Checked;
        _settings.CaptureDrag = _drag.Checked;
        _settings.HideAppFromCaptures = _hideApp.Checked;
        _settings.RedactPasswords = _redactPasswords.Checked;
        _settings.RedactPatterns = _redactPatterns.Lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        _settings.AutoZoom = _autoZoom.Checked;
        _settings.ZoomPadding = (int)_padding.Value;
        _settings.ShowClickMarker = _marker.Checked;
        _settings.ShowElementOutline = _outline.Checked;
        _settings.AddHeadingOnAppChange = _headings.Checked;
        _settings.DarkTheme = _darkTheme.Checked;
        _settings.MarkerColor = StepRenderer.ToHex(_chosenMarker);

        _settings.HotkeyStartPause = KeyOf(_keyStart);
        _settings.HotkeyStop = KeyOf(_keyStop);
        _settings.HotkeyShot = KeyOf(_keyShot);
        _settings.HotkeyNeedsModifiers = _keyModifiers.Checked;

        _settings.AiEnabled = _aiEnabled.Checked;
        _settings.AiProvider = SelectedProvider.Id;
        _settings.AiBaseUrl = _aiBaseUrl.Text.Trim();
        _settings.AiModel = _aiModel.Text.Trim();
        _settings.AiSendScreenshots = _aiPictures.Checked;
        _settings.AiWriteNotes = _aiNotes.Checked;

        if (_keyEdited)
        {
            _settings.SetAiKey(_aiKey.Text.Trim());
        }

        _settings.Save();
        DialogResult = DialogResult.OK;
    }

    private static void Configure(NumericUpDown control, int min, int max, int value, int step = 1)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Increment = step;
        control.Value = Math.Clamp(value, min, max);
    }

    private static void FillKeys(ComboBox combo, int selected)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        for (int key = 0x70; key <= 0x7B; key++)
        {
            combo.Items.Add("F" + (key - 0x6F));
        }

        int index = Math.Clamp(selected - 0x70, 0, combo.Items.Count - 1);
        combo.SelectedIndex = index;
    }

    private static int KeyOf(ComboBox combo) => 0x70 + Math.Max(0, combo.SelectedIndex);

    private static void AddRow(Control page, string label, Control input, ref int y)
    {
        page.Controls.Add(new Label
        {
            Text = label,
            Bounds = new Rectangle(16, y, 400, 18),
            ForeColor = Theme.Muted,
        });

        input.Bounds = new Rectangle(16, y + 20, input is NumericUpDown ? 110 : 496, 26);
        if (input is ComboBox box)
        {
            box.Width = 496;
        }

        page.Controls.Add(input);
        y += 56;
    }

    private static void AddInline(Control page, string label, Control input, int x, int y)
    {
        page.Controls.Add(new Label
        {
            Text = label,
            Bounds = new Rectangle(x, y, 160, 18),
            ForeColor = Theme.Muted,
        });

        input.Bounds = new Rectangle(x, y + 20, 150, 26);
        page.Controls.Add(input);
    }

    private static void AddCheck(Control page, CheckBox box, string label, bool value, ref int y)
    {
        box.Text = label;
        box.Checked = value;
        box.Bounds = new Rectangle(16, y, 496, 24);
        page.Controls.Add(box);
        y += 28;
    }
}

internal static class StepwrightText
{
    public static string Shorten(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string clean = value.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return clean.Length <= max ? clean : clean[..max] + "...";
    }
}
