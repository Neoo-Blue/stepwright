using Stepwright.Ai;
using Stepwright.Config;
using Stepwright.Web;
using Stepwright.Model;
using Stepwright.Publish;
using Stepwright.Render;

namespace Stepwright.Ui;

/// <summary>
/// Every page is a single column table that sizes itself to its contents, so a caption can
/// never be clipped and nothing depends on where a pixel happened to land. The pages scroll
/// when the display font is large enough to need it.
/// </summary>
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
    private readonly ComboBox _gifMotion = new();
    private readonly NumericUpDown _gifWidth = new();

    private readonly ComboBox _keyStart = new();
    private readonly ComboBox _keyStop = new();
    private readonly ComboBox _keyShot = new();
    private readonly CheckBox _keyModifiers = new();

    private readonly CheckBox _aiEnabled = new();
    private readonly ComboBox _aiProvider = new();
    private readonly ComboBox _aiAuth = new();
    private readonly TextBox _aiBaseUrl = new();
    private readonly ComboBox _aiModel = new();
    private readonly TextBox _aiKey = new();
    private readonly TextBox _aiToken = new();
    private readonly TextBox _aiCliPath = new();
    private readonly Label _aiCliStatus = new();
    private readonly TextBox _aiAppId = new();
    private readonly TextBox _aiTenant = new();
    private readonly Label _aiSignedIn = new();
    private readonly Label _aiClaudeSignedIn = new();
    private readonly Label _aiSubscriptionNote = new();
    private readonly Label _aiBrowserSignedIn = new();
    private readonly ComboBox _aiCopilotKind = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _aiModelNote = new();
    private readonly Label _aiPictureNote = new();
    private readonly List<string> _authKinds = new();
    private readonly CheckBox _aiPictures = new();
    private readonly CheckBox _aiNotes = new();
    private readonly Button _aiModels = new();
    private readonly Button _aiTest = new();
    private readonly Label _aiResult = new();
    private readonly LinkLabel _aiKeyLink = new();
    private readonly Label _aiHint = new();

    private readonly ComboBox _exportFormat = new();
    private readonly Label _formatDetail = new();

    private readonly TextBox _huduUrl = new();
    private readonly TextBox _huduKey = new();
    private readonly ComboBox _huduPublish = new();
    private readonly Label _huduWebNote = new();
    private readonly Label _policyNote = new();
    private readonly Label _huduResult = new();
    private readonly ComboBox _confluenceAuth = new();
    private readonly TextBox _confluenceSite = new();
    private readonly TextBox _confluenceEmail = new();
    private readonly TextBox _confluenceToken = new();
    private readonly TextBox _confluenceClientId = new();
    private readonly TextBox _confluenceSecret = new();
    private readonly Label _confluenceSignedIn = new();
    private readonly Label _confluenceResult = new();
    private readonly TableLayoutPanel _confluenceTokenGroup = Group();
    private readonly TableLayoutPanel _confluenceOAuthGroup = Group();

    private readonly TableLayoutPanel _aiKeyGroup = Group();
    private readonly TableLayoutPanel _aiMicrosoftGroup = Group();
    private readonly TableLayoutPanel _aiCliGroup = Group();
    private readonly TableLayoutPanel _aiTokenGroup = Group();
    private readonly TableLayoutPanel _aiSubscriptionGroup = Group();
    private readonly TableLayoutPanel _aiBrowserGroup = Group();

    private Color _chosenMarker;
    private bool _keyEdited;
    private bool _tokenEdited;
    private bool _confluenceSecretEdited;
    private bool _huduKeyEdited;
    private bool _confluenceTokenEdited;

    public SettingsForm(AppSettings settings)
    {
        _settings = settings;
        _chosenMarker = StepRenderer.Parse(settings.MarkerColor, Color.OrangeRed);

        Text = Build.Titled("Stepwright settings");
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 780);
        MinimumSize = new Size(580, 520);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        AutoScaleMode = AutoScaleMode.Dpi;

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(16, 8),
        };

        tabs.TabPages.Add(BuildRecordingTab());
        tabs.TabPages.Add(BuildLookTab());
        tabs.TabPages.Add(BuildAssistantTab());
        tabs.TabPages.Add(BuildFormatTab());
        tabs.TabPages.Add(BuildPublishingTab());

        Controls.Add(tabs);
        Controls.Add(BuildFooter());

        Load += (_, _) =>
        {
            Theme.Apply(this);
            Theme.StyleWindow(Handle);
            ReloadAuthChoices(_settings.AiAuth);
            ShowProviderHint();
            ShowConfluenceRoute();
            ApplyPolicy();
        };
    }

    /// <summary>
    /// Puts the page under whatever an administrator decided for this machine. A setting that is
    /// fixed is shown, greyed, and said to be theirs rather than the person's, which is kinder
    /// than a box that takes an edit and quietly throws it away. A key they sealed is not shown at
    /// all, because it is not in this app to show.
    /// </summary>
    private void ApplyPolicy()
    {
        Policy policy = Policy.Current;

        if (!policy.Exists)
        {
            return;
        }

        string who = policy.Who;

        void Fix(Control control, string? value)
        {
            if (policy.Fixed(value))
            {
                control.Enabled = false;
            }
        }

        Fix(_aiProvider, policy.AiProvider);
        Fix(_aiAuth, policy.AiAuth);
        Fix(_aiBaseUrl, policy.AiBaseUrl);
        Fix(_aiModel, policy.AiModel);
        Fix(_aiAppId, policy.AiAppId);
        Fix(_aiTenant, policy.AiTenant);
        Fix(_huduUrl, policy.HuduBaseUrl);
        Fix(_huduPublish, policy.HuduPublish);
        Fix(_confluenceSite, policy.ConfluenceSite);
        Fix(_confluenceEmail, policy.ConfluenceEmail);

        // A sealed key is never handed to a box, not even as stars, because stars in a box are
        // one careless change away from being replaced and one clever one away from being read.
        if (!string.IsNullOrWhiteSpace(policy.AiKeyProtected))
        {
            _aiKey.Enabled = false;
            _aiKey.UseSystemPasswordChar = false;
            _aiKey.Text = "Set by " + who + ", and not shown here";
        }

        if (!string.IsNullOrWhiteSpace(policy.HuduKeyProtected))
        {
            _huduKey.Enabled = false;
            _huduKey.UseSystemPasswordChar = false;
            _huduKey.Text = "Set by " + who + ", and not shown here";
        }

        if (!string.IsNullOrWhiteSpace(policy.ConfluenceToken()))
        {
            _confluenceToken.Enabled = false;
            _confluenceToken.UseSystemPasswordChar = false;
            _confluenceToken.Text = "Set by " + who + ", and not shown here";
        }

        _policyNote.Text = policy.Locked
            ? "Some of these settings were set for this machine by " + who + ". They are filled in"
              + " and cannot be changed here. Any key set that way is used but never shown, and is"
              + " not kept in your own settings file."
            : who + " filled some of these in for you as a starting point. You may change them.";

        _policyNote.Visible = true;
    }

    private Control BuildFooter()
    {
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 60,
            BackColor = Theme.Panel,
            Padding = new Padding(16, 13, 16, 13),
        };

        // Said along the bottom rather than on one page, because a policy reaches several pages
        // and a person who lands on the wrong one should still know why a box will not take an edit.
        _policyNote.Dock = DockStyle.Bottom;
        _policyNote.AutoSize = false;
        _policyNote.Height = 34;
        _policyNote.ForeColor = Theme.Muted;
        _policyNote.Font = Theme.UiSmall;
        _policyNote.Padding = new Padding(16, 2, 16, 2);
        _policyNote.BackColor = Theme.Panel;
        _policyNote.Visible = false;
        Controls.Add(_policyNote);

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Panel,
            WrapContents = false,
        };

        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(92, 32) };
        var save = new Button { Text = "Save", AutoSize = true, MinimumSize = new Size(92, 32), Tag = "primary" };

        Theme.StyleButton(save, primary: true);
        Theme.StyleButton(cancel);

        save.Click += (_, _) => Commit();
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;

        row.Controls.Add(cancel);
        row.Controls.Add(save);
        footer.Controls.Add(row);

        AcceptButton = save;
        CancelButton = cancel;
        return footer;
    }

    // ------------------------------------------------------------------ pages

    private TabPage BuildRecordingTab()
    {
        (TabPage page, TableLayoutPanel table) = NewPage("Recording");

        _author.Text = _settings.Author;
        AddField(table, "Your name", _author);

        Configure(_countdown, 0, 10, _settings.CountdownSeconds);
        AddField(table, "Countdown before recording, in seconds", _countdown, narrow: true);

        Configure(_typingMerge, 400, 6000, _settings.TypingMergeMilliseconds, 100);
        AddField(table, "Wait before a burst of typing becomes a step, in milliseconds", _typingMerge, narrow: true);

        AddCheck(table, _allMonitors, "Capture every monitor instead of the one in use", _settings.CaptureAllMonitors);
        AddCheck(table, _keyboard, "Record what is typed", _settings.CaptureKeyboard);
        AddCheck(table, _scroll, "Record scrolling", _settings.CaptureScroll);
        AddCheck(table, _drag, "Record dragging", _settings.CaptureDrag);
        AddCheck(table, _hideApp, "Keep Stepwright itself out of the screenshots", _settings.HideAppFromCaptures);
        AddCheck(table, _redactPasswords, "Never store anything typed into a password box", _settings.RedactPasswords);

        _redactPatterns.Multiline = true;
        _redactPatterns.ScrollBars = ScrollBars.Vertical;
        _redactPatterns.Height = 76;
        _redactPatterns.Text = string.Join(Environment.NewLine, _settings.RedactPatterns);
        AddField(table, "Hide anything matching these patterns, one per line", _redactPatterns);

        AddHeading(table, "Shortcuts");

        FillKeys(_keyStart, _settings.HotkeyStartPause);
        FillKeys(_keyStop, _settings.HotkeyStop);
        FillKeys(_keyShot, _settings.HotkeyShot);

        var keys = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        for (int i = 0; i < 3; i++)
        {
            keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        }

        keys.Controls.Add(KeyCell("Start or pause", _keyStart), 0, 0);
        keys.Controls.Add(KeyCell("Finish", _keyStop), 1, 0);
        keys.Controls.Add(KeyCell("Capture now", _keyShot), 2, 0);

        table.Controls.Add(keys);

        AddCheck(table, _keyModifiers, "Also hold Ctrl and Shift for these shortcuts", _settings.HotkeyNeedsModifiers);
        return page;
    }

    private TabPage BuildLookTab()
    {
        (TabPage page, TableLayoutPanel table) = NewPage("Look");

        AddCheck(table, _autoZoom, "Zoom each screenshot to the part that was used", _settings.AutoZoom);

        Configure(_padding, 80, 900, _settings.ZoomPadding, 20);
        AddField(table, "Space to keep around the zoomed area, in pixels", _padding, narrow: true);

        AddCheck(table, _marker, "Mark where the click landed", _settings.ShowClickMarker);
        AddCheck(table, _outline, "Outline the control that was used", _settings.ShowElementOutline);
        AddCheck(table, _headings, "Start a new section when the application changes", _settings.AddHeadingOnAppChange);
        AddCheck(table, _darkTheme, "Dark window colours", _settings.DarkTheme);

        _markerColor.Size = new Size(96, 30);
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

        AddField(table, "Marker colour", _markerColor, narrow: true);
        AddNote(table, "A change of colours takes effect the next time Stepwright starts.");

        AddHeading(table, "Animated steps");

        AddNote(
            table,
            "A step can export as a short animation that starts wide and settles on the control"
            + " that was used. Turn it on for a step with the Animate button while editing. There"
            + " is nothing else to set up, and these two only exist if you want to nudge it.");

        _gifMotion.DropDownStyle = ComboBoxStyle.DropDownList;
        _gifMotion.Items.AddRange(new object[] { "Gentle", "Normal", "Quick" });
        _gifMotion.SelectedItem = _settings.GifMotion is "Gentle" or "Quick" ? _settings.GifMotion : "Normal";
        AddField(table, "How lively the movement is", _gifMotion, narrow: true);
        _gifMotion.Width = 160;

        Configure(_gifWidth, 320, 1400, _settings.GifWidth, 20);
        AddField(table, "Widest an animation is written, in pixels", _gifWidth, narrow: true);

        return page;
    }

    private TabPage BuildAssistantTab()
    {
        (TabPage page, TableLayoutPanel table) = NewPage("Assistant");

        AddNote(
            table,
            "Optional. The assistant rewrites the wording of every step and can add a short note"
            + " where one helps. Nothing is sent anywhere until you turn this on.");

        AddCheck(table, _aiEnabled, "Use the assistant", _settings.AiEnabled);

        _aiProvider.DropDownStyle = ComboBoxStyle.DropDownList;
        foreach (AiProvider provider in AiProviders.All)
        {
            _aiProvider.Items.Add(provider.Label);
        }

        _aiProvider.SelectedIndex = Math.Max(
            0,
            AiProviders.All.ToList().FindIndex(p => p.Id == _settings.AiProvider));
        _aiProvider.SelectedIndexChanged += (_, _) => ApplyProviderPreset();

        AddField(table, "Service", _aiProvider);

        _aiAuth.DropDownStyle = ComboBoxStyle.DropDownList;
        _aiAuth.SelectedIndexChanged += (_, _) => ShowAuthRoute();
        AddField(table, "How it signs in", _aiAuth);

        BuildKeyGroup();
        BuildCliGroup();
        BuildTokenGroup();
        BuildSubscriptionGroup();
        BuildBrowserGroup();
        BuildMicrosoftGroup();

        table.Controls.Add(_aiKeyGroup);
        table.Controls.Add(_aiCliGroup);
        table.Controls.Add(_aiSubscriptionGroup);
        table.Controls.Add(_aiBrowserGroup);
        table.Controls.Add(_aiTokenGroup);
        table.Controls.Add(_aiMicrosoftGroup);

        // The model is a list you can also type into, filled by asking the service.
        _aiModel.DropDownStyle = ComboBoxStyle.DropDown;
        _aiModel.Text = _settings.AiModel;

        var modelRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modelRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _aiModel.Dock = DockStyle.Fill;
        _aiModel.Margin = new Padding(0, 2, 8, 0);

        _aiModels.Text = "Find models";
        _aiModels.AutoSize = true;
        _aiModels.MinimumSize = new Size(116, 28);
        _aiModels.Margin = new Padding(0, 2, 0, 0);
        Theme.StyleButton(_aiModels);
        _aiModels.Click += async (_, _) => await LoadModelsAsync().ConfigureAwait(true);

        modelRow.Controls.Add(_aiModel, 0, 0);
        modelRow.Controls.Add(_aiModels, 1, 0);

        table.Controls.Add(Caption("Model"));
        table.Controls.Add(modelRow);

        _aiModelNote.AutoSize = true;
        _aiModelNote.MaximumSize = new Size(580, 0);
        _aiModelNote.ForeColor = Theme.Muted;
        _aiModelNote.Font = Theme.UiSmall;
        _aiModelNote.Margin = new Padding(1, 2, 0, 12);
        _aiModelNote.BackColor = Color.Transparent;
        table.Controls.Add(_aiModelNote);

        AddCheck(table, _aiPictures, "Let the assistant see each screenshot", _settings.AiSendScreenshots);

        _aiPictureNote.AutoSize = true;
        _aiPictureNote.MaximumSize = new Size(580, 0);
        _aiPictureNote.ForeColor = Theme.Muted;
        _aiPictureNote.Font = Theme.UiSmall;
        _aiPictureNote.Margin = new Padding(1, 2, 0, 12);
        _aiPictureNote.BackColor = Color.Transparent;
        table.Controls.Add(_aiPictureNote);

        AddCheck(table, _aiNotes, "Write a note under a step when it helps", _settings.AiWriteNotes);

        _aiTest.Text = "Test the connection";
        _aiTest.AutoSize = true;
        _aiTest.MinimumSize = new Size(152, 30);
        _aiTest.Margin = new Padding(0, 10, 0, 0);
        Theme.StyleButton(_aiTest);
        _aiTest.Click += async (_, _) => await TestAsync().ConfigureAwait(true);
        table.Controls.Add(_aiTest);

        _aiResult.AutoSize = false;
        _aiResult.Height = 44;
        _aiResult.Dock = DockStyle.Fill;
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Margin = new Padding(0, 8, 0, 0);
        table.Controls.Add(_aiResult);

        return page;
    }

    /// <summary>The fields a bought key needs: an address, the key, and where to get one.</summary>
    private void BuildKeyGroup()
    {
        _aiBaseUrl.Text = _settings.AiBaseUrl;
        AddField(_aiKeyGroup, "Address", _aiBaseUrl);

        _aiKey.UseSystemPasswordChar = true;
        _aiKey.Text = _settings.HasAiKey ? new string('*', 24) : string.Empty;

        // Tracked rather than guessed from the value, because a real key may contain anything.
        _aiKey.TextChanged += (_, _) => _keyEdited = true;
        AddField(_aiKeyGroup, "Key, stored encrypted for this Windows account", _aiKey);

        var keyRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        _aiHint.AutoSize = true;
        _aiHint.ForeColor = Theme.Muted;
        _aiHint.Font = Theme.UiSmall;
        _aiHint.Margin = new Padding(0, 5, 14, 0);

        _aiKeyLink.AutoSize = true;
        _aiKeyLink.Text = "Where to get a key";
        _aiKeyLink.Font = Theme.UiSmall;
        _aiKeyLink.Margin = new Padding(0, 5, 0, 0);
        _aiKeyLink.LinkClicked += (_, _) => OpenKeyPage();

        keyRow.Controls.Add(_aiHint);
        keyRow.Controls.Add(_aiKeyLink);
        _aiKeyGroup.Controls.Add(keyRow);
    }

    /// <summary>
    /// The subscription route. Stepwright runs the app you already signed in to, the same way
    /// you would at a prompt, so the work comes out of the plan you already pay for.
    /// </summary>
    private void BuildCliGroup()
    {
        AddNote(
            _aiCliGroup,
            "Stepwright runs the app on this machine and reads what it says back. No token is"
            + " kept here, and nothing is billed by the token.");

        _aiCliStatus.AutoSize = true;
        _aiCliStatus.MaximumSize = new Size(580, 0);
        _aiCliStatus.ForeColor = Theme.Muted;
        _aiCliStatus.Margin = new Padding(1, 2, 0, 8);
        _aiCliStatus.BackColor = Color.Transparent;
        _aiCliGroup.Controls.Add(_aiCliStatus);

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        var check = Action("Check the app", async () => await CheckAgentAsync().ConfigureAwait(true));
        var signIn = Action("Sign in", SignInToAgent);
        var install = Action("How to install it", OpenAgentPage);

        row.Controls.Add(check);
        row.Controls.Add(signIn);
        row.Controls.Add(install);
        _aiCliGroup.Controls.Add(row);

        _aiCliPath.Text = _settings.AiCliPath;
        AddField(_aiCliGroup, "Where the app is, only when it lives somewhere unusual", _aiCliPath);
    }

    /// <summary>The advanced route, and the warning that belongs with it.</summary>
    /// <summary>
    /// Signing in to a Claude subscription with nothing installed and nothing registered. The
    /// browser opens on Anthropic's own page, the person signs in there, and Anthropic shows one
    /// line to paste back. From then on the app renews itself and this page is never needed
    /// again.
    /// </summary>
    private void BuildSubscriptionGroup()
    {
        _aiSubscriptionNote.AutoSize = true;
        _aiSubscriptionNote.MaximumSize = new Size(580, 0);
        _aiSubscriptionNote.ForeColor = Theme.Muted;
        _aiSubscriptionNote.Font = Theme.UiSmall;
        _aiSubscriptionNote.Margin = new Padding(1, 0, 0, 10);
        _aiSubscriptionNote.BackColor = Color.Transparent;
        _aiSubscriptionGroup.Controls.Add(_aiSubscriptionNote);

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        row.Controls.Add(Action("Sign in", async () => await SignInSubscriptionAsync().ConfigureAwait(true)));
        row.Controls.Add(Action("Sign out", SignOutSubscription));
        row.Controls.Add(Action("See the plans", OpenSubscriptionPlans));

        _aiSubscriptionGroup.Controls.Add(row);

        _aiClaudeSignedIn.AutoSize = true;
        _aiClaudeSignedIn.MaximumSize = new Size(580, 0);
        _aiClaudeSignedIn.ForeColor = Theme.Muted;
        _aiClaudeSignedIn.Font = Theme.UiSmall;
        _aiClaudeSignedIn.Margin = new Padding(1, 0, 0, 10);
        _aiClaudeSignedIn.BackColor = Color.Transparent;
        _aiSubscriptionGroup.Controls.Add(_aiClaudeSignedIn);

        ShowSubscriptionSignIn();
    }

    /// <summary>The service the subscription route currently points at, by the chosen provider.</summary>
    private string SubscriptionService => SelectedProvider.Id switch
    {
        AiProviders.OpenAi => "ChatGPT",
        AiProviders.Gemini => "Gemini",
        _ => "Claude",
    };

    private void ShowSubscriptionSignIn()
    {
        string service = SubscriptionService;

        _aiSubscriptionNote.Text = service == "Claude"
            ? "Press sign in, sign in to Claude in the browser as you normally would, then paste"
              + " back the line it shows you. That is the whole setup. There is nothing to install,"
              + " and the work is paid for by the Claude plan you already have."
            : "Press sign in and sign in to " + service + " in the browser. Read the caution below"
              + " first: this reaches a consumer subscription the way the vendor's own command line"
              + " app does, which is outside the terms of a personal plan. The plainly sanctioned"
              + " route is that app, signed in, which Stepwright can also drive.";

        bool has = _settings.HasSubscriptionSignIn;

        _aiClaudeSignedIn.ForeColor = has ? Theme.Good : Theme.Muted;
        _aiClaudeSignedIn.Text = has
            ? "Signed in"
              + (string.IsNullOrWhiteSpace(_settings.AiAccount) ? string.Empty : " as " + _settings.AiAccount)
              + (string.IsNullOrWhiteSpace(_settings.AiPlan) ? string.Empty : " on the " + _settings.AiPlan + " plan")
              + ". Stepwright keeps this signed in on its own from now on."
            : "Not signed in yet.";
    }

    private void OpenSubscriptionPlans() => Open(SelectedProvider.Id switch
    {
        AiProviders.OpenAi => ChatGptOAuth.PlansPage,
        AiProviders.Gemini => GeminiOAuth.PlansPage,
        _ => ClaudeOAuth.PlansPage,
    });

    /// <summary>
    /// The sign in itself, for whichever service the provider points at. Claude shows a code on a
    /// page to paste back; ChatGPT and Gemini come back to a door on this machine, so there is
    /// nothing to paste for those two.
    /// </summary>
    private async Task SignInSubscriptionAsync()
    {
        try
        {
            switch (SelectedProvider.Id)
            {
                case AiProviders.OpenAi:
                    await SignInChatGptAsync().ConfigureAwait(true);
                    break;
                case AiProviders.Gemini:
                    await SignInGeminiAsync().ConfigureAwait(true);
                    break;
                default:
                    await SignInClaudeAsync().ConfigureAwait(true);
                    break;
            }
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 220);
        }
    }

    private async Task SignInClaudeAsync()
    {
        ClaudeOAuth.Attempt attempt = ClaudeOAuth.Begin();
        Open(attempt.Address);

        string pasted = Ask(
            "Sign in to Claude",
            "Your browser is open on Anthropic's sign in page. Sign in there, then paste the"
            + " line it gives you here.",
            string.Empty);

        if (string.IsNullOrWhiteSpace(pasted))
        {
            _aiResult.ForeColor = Theme.Muted;
            _aiResult.Text = "Sign in cancelled.";
            return;
        }

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Finishing the sign in...";

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        ClaudeSession session = await ClaudeOAuth
            .FinishAsync(attempt, pasted, cancel.Token)
            .ConfigureAwait(true);

        _settings.AiAuth = AiAuthKinds.Subscription;
        _settings.AiProvider = AiProviders.Anthropic;
        _settings.AiBaseUrl = AiProviders.Find(AiProviders.Anthropic).BaseUrl;
        _settings.RememberClaude(session);
        _settings.Save();

        _aiBaseUrl.Text = _settings.AiBaseUrl;
        ShowSubscriptionSignIn();

        _aiResult.ForeColor = Theme.Good;
        _aiResult.Text = "Signed in. Test the connection to prove it works.";
    }

    private async Task SignInChatGptAsync()
    {
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Opening the ChatGPT sign in...";

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        ChatGptSession session = await ChatGptOAuth.SignInAsync(Open, cancel.Token).ConfigureAwait(true);

        _settings.AiAuth = AiAuthKinds.Subscription;
        _settings.AiProvider = AiProviders.OpenAi;
        _settings.RememberChatGpt(session);
        _settings.Save();

        ShowSubscriptionSignIn();

        _aiResult.ForeColor = Theme.Good;
        _aiResult.Text = "Signed in. Test the connection to prove it works.";
    }

    private async Task SignInGeminiAsync()
    {
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Opening the Google sign in...";

        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(6));

        GeminiSession session = await GeminiOAuth.SignInAsync(Open, cancel.Token).ConfigureAwait(true);

        _settings.AiAuth = AiAuthKinds.Subscription;
        _settings.AiProvider = AiProviders.Gemini;
        _settings.RememberGemini(session);
        _settings.Save();

        ShowSubscriptionSignIn();

        _aiResult.ForeColor = Theme.Good;
        _aiResult.Text = "Signed in. Test the connection to prove it works.";
    }

    private void SignOutSubscription()
    {
        _settings.ForgetSubscription();
        _settings.Save();
        ShowSubscriptionSignIn();

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Signed out.";
    }

    /// <summary>
    /// Copilot through its own page. There is nothing to register and nobody to ask, because as
    /// far as Microsoft is concerned nothing here is an application: it is the person, signed in,
    /// using the licence they already have.
    /// </summary>
    private void BuildBrowserGroup()
    {
        AddNote(
            _aiBrowserGroup,
            "Press sign in, sign in to Copilot in the window that opens exactly as you would in"
            + " your browser, and close it. That is the whole setup. Nothing is registered with"
            + " Microsoft, no administrator has to approve anything, and Stepwright never sees"
            + " your password: what it keeps is what a browser keeps, a signed in profile held"
            + " under this Windows account.");

        _aiCopilotKind.Items.AddRange(new object[]
        {
            "Copilot that comes with my work or school account",
            "Copilot on my personal account",
        });

        _aiCopilotKind.SelectedIndex = _settings.AiCopilotWork ? 0 : 1;
        AddField(_aiBrowserGroup, "Which Copilot", _aiCopilotKind);

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        row.Controls.Add(Action("Sign in to Copilot", async () => await SignInBrowserAsync().ConfigureAwait(true)));
        row.Controls.Add(Action("Sign out", SignOutBrowser));
        row.Controls.Add(Action("Save a page report", async () => await SavePageReportAsync().ConfigureAwait(true)));

        _aiBrowserGroup.Controls.Add(row);

        _aiBrowserSignedIn.AutoSize = true;
        _aiBrowserSignedIn.MaximumSize = new Size(580, 0);
        _aiBrowserSignedIn.Font = Theme.UiSmall;
        _aiBrowserSignedIn.Margin = new Padding(1, 0, 0, 10);
        _aiBrowserSignedIn.BackColor = Color.Transparent;
        _aiBrowserGroup.Controls.Add(_aiBrowserSignedIn);

        AddNote(
            _aiBrowserGroup,
            "This route reads Copilot's own page, so it is slower than a key and it depends on"
            + " that page staying roughly as it is. If Microsoft redesigns it enough to break"
            + " this, Stepwright says so plainly rather than inventing an answer, and the work"
            + " account route below keeps working.");

        ShowBrowserSignIn();
    }

    private void ShowBrowserSignIn()
    {
        bool ready = CopilotWeb.Remembered;

        _aiBrowserSignedIn.ForeColor = ready ? Theme.Good : Theme.Muted;
        _aiBrowserSignedIn.Text = ready
            ? "Signed in on this machine. Stepwright stays signed in until you sign out here."
            : "Not signed in yet.";
    }

    private async Task SignInBrowserAsync()
    {
        if (!WebSession.Available)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = WebSession.Missing;
            return;
        }

        _settings.AiCopilotWork = _aiCopilotKind.SelectedIndex != 1;

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Opening Copilot...";

        try
        {
            bool landed = await CopilotWeb
                .SignInAsync(this, _settings.AiCopilotWork)
                .ConfigureAwait(true);

            _settings.AiAuth = AiAuthKinds.Browser;
            _settings.AiProvider = AiProviders.Copilot;
            _settings.Save();

            ShowBrowserSignIn();

            _aiResult.ForeColor = landed ? Theme.Good : Theme.Muted;
            _aiResult.Text = landed
                ? "Signed in. Test the connection to prove it works."
                : "The window was closed. If you did sign in, test the connection anyway.";
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 220);
        }
    }

    /// <summary>
    /// Writes down everything the Copilot page holds after a question, so the reading of the
    /// answer can be built against what is really there. The file is opened for the person to
    /// look at and send on.
    /// </summary>
    private async Task SavePageReportAsync()
    {
        if (!Web.WebSession.Available)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = Web.WebSession.Missing;
            return;
        }

        if (!Web.CopilotWeb.Remembered)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = "Sign in to Copilot first, then save a page report.";
            return;
        }

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Asking Copilot, then writing down what the page holds...";

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(3));

            string report = await Web.CopilotWeb
                .DiagnoseAsync(_settings.AiCopilotWork, "Stepwright page report probe. Reply with a short sentence.", cancel.Token)
                .ConfigureAwait(true);

            Directory.CreateDirectory(AppSettings.SettingsFolder);
            string path = Path.Combine(AppSettings.SettingsFolder, "copilot-page-report.txt");
            await File.WriteAllTextAsync(path, report, cancel.Token).ConfigureAwait(true);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Opening it is a convenience; the file is written either way.
            }

            _aiResult.ForeColor = Theme.Good;
            _aiResult.Text = "Page report written to " + path + ". Open it and send it over.";
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 200);
        }
    }

    private void SignOutBrowser()
    {
        CopilotWeb.SignOut();
        ShowBrowserSignIn();

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Signed out of Copilot on this machine.";
    }

    /// <summary>
    /// Asks for one line of text. The box is wide and the dialog stays put while the person goes
    /// off to the browser and comes back, because that round trip is the whole point of it.
    /// </summary>
    private string Ask(string caption, string message, string initial)
    {
        using var dialog = new Form
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(520, 176),
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.Window,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
        };

        var text = new Label
        {
            Text = message,
            Bounds = new Rectangle(16, 14, 488, 60),
            ForeColor = Theme.Muted,
        };

        var input = new TextBox
        {
            Text = initial,
            Bounds = new Rectangle(16, 82, 488, 26),
            BackColor = Theme.Raised,
            ForeColor = Theme.Text,
        };

        var ok = new Button { Text = "Finish", Bounds = new Rectangle(344, 124, 76, 30), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Bounds = new Rectangle(428, 124, 76, 30), DialogResult = DialogResult.Cancel };
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

    private void BuildTokenGroup()
    {
        AddNote(
            _aiTokenGroup,
            "Advanced. A subscription token is issued for the vendor's own app, and sending it"
            + " from anything else is outside the terms of a consumer plan. Accounts have been"
            + " suspended for it. The safe route is the app above, or a key.");

        _aiToken.UseSystemPasswordChar = true;
        _aiToken.Text = _settings.HasAiToken ? new string('*', 24) : string.Empty;
        _aiToken.TextChanged += (_, _) => _tokenEdited = true;
        AddField(_aiTokenGroup, "Token, stored encrypted for this Windows account", _aiToken);

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        row.Controls.Add(Action("Make a token for me", MakeToken));
        _aiTokenGroup.Controls.Add(row);

        AddNote(
            _aiTokenGroup,
            "That opens a window running claude setup-token, which signs you in through your"
            + " browser and then prints a token. Copy it and paste it above. It goes to the same"
            + " address as a key, so leave the address as it is.");
    }

    /// <summary>
    /// Claude Code is the only thing that can mint one of these, so the button runs it rather
    /// than sending the person off to find the instructions.
    /// </summary>
    private void MakeToken() => RunInConsole(
        "claude setup-token",
        "A window is open running claude setup-token. Sign in when the browser asks, then copy"
        + " the token it prints and paste it into the token box.");

    private void SignInToAgent()
    {
        AiAgent? agent = AiAgents.Find(SelectedProvider.Id);

        if (agent is null)
        {
            return;
        }

        RunInConsole(
            agent.SignInCommand,
            $"A window is open running {agent.SignInCommand}. Sign in there with your"
            + $" {agent.Plan} account, close it, then press Check the app.");
    }

    /// <summary>
    /// Signing in with a work account. Microsoft issues these tokens to an application, so the
    /// application is registered once in your own tenant and its identifier goes here. Nothing
    /// secret is kept: the sign in is the device code flow, which is why there is a code to
    /// type rather than an address to register.
    /// </summary>
    private void BuildMicrosoftGroup()
    {
        if (Connect.HasMicrosoft)
        {
            AddNote(
                _aiMicrosoftGroup,
                "Press sign in and follow the browser. There is nothing to register: this copy of"
                + " Stepwright already has an application. The first person at your company to"
                + " use it may be told that an administrator has to approve it once, and after"
                + " that everybody simply signs in.");
        }
        else
        {
            AddNote(
                _aiMicrosoftGroup,
                "Register an application once in Microsoft Entra, allow it to be a public client,"
                + " and grant it the Graph permissions the service needs. Then sign in here and"
                + " Stepwright renews it on its own.");

            _aiAppId.Text = _settings.AiAppId;
            AddField(_aiMicrosoftGroup, "Application identifier", _aiAppId);
        }

        _aiTenant.Text = _settings.AiTenant;
        AddField(
            _aiMicrosoftGroup,
            "Tenant, only when your organisation needs it named. Blank means any work account",
            _aiTenant);

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        row.Controls.Add(Action("Sign in with Microsoft", async () => await SignInMicrosoftAsync().ConfigureAwait(true)));
        row.Controls.Add(Action("Sign out", SignOutMicrosoft));
        row.Controls.Add(Action("Open the portal", () => Open(MicrosoftOAuth.PortalPage)));

        _aiMicrosoftGroup.Controls.Add(row);

        _aiSignedIn.AutoSize = true;
        _aiSignedIn.MaximumSize = new Size(580, 0);
        _aiSignedIn.ForeColor = Theme.Muted;
        _aiSignedIn.Font = Theme.UiSmall;
        _aiSignedIn.Margin = new Padding(1, 0, 0, 10);
        _aiSignedIn.BackColor = Color.Transparent;
        _aiMicrosoftGroup.Controls.Add(_aiSignedIn);

        ShowMicrosoftSignIn();
    }

    private void ShowMicrosoftSignIn()
    {
        _aiSignedIn.ForeColor = _settings.HasMicrosoftSignIn ? Theme.Good : Theme.Muted;
        _aiSignedIn.Text = _settings.HasMicrosoftSignIn
            ? "Signed in"
              + (string.IsNullOrWhiteSpace(_settings.AiAccount) ? string.Empty : " as " + _settings.AiAccount)
              + ". Stepwright renews this on its own, and refuses to carry on if the account"
              + " later belongs to a different organisation."
            : "Not signed in yet.";
    }

    /// <summary>
    /// Runs the device code flow. The code appears in a window the person can leave open while
    /// they sign in on whichever machine has their browser, which is the point of this flow.
    /// </summary>
    private async Task SignInMicrosoftAsync()
    {
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Asking Microsoft for a code...";

        try
        {
            string[] scopes = SelectedProvider.Id == AiProviders.Copilot
                ? MicrosoftOAuth.CopilotScopes
                : MicrosoftOAuth.FoundryScopes;

            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));

            string appId = _aiAppId.Text.Trim().Length > 0 ? _aiAppId.Text.Trim() : Connect.MicrosoftAppId;

            MicrosoftSession session = await MicrosoftOAuth
                .SignInAsync(
                    appId,
                    _aiTenant.Text.Trim(),
                    scopes,
                    (code, where) =>
                    {
                        _aiResult.ForeColor = Theme.Good;
                        _aiResult.Text = $"Enter the code {code} at {where}. Waiting...";

                        try
                        {
                            Clipboard.SetText(code);
                        }
                        catch
                        {
                            // A clipboard another application is holding is no reason to stop.
                        }

                        Open(where);
                    },
                    cancel.Token)
                .ConfigureAwait(true);

            // Kept straight away, because a sign in that is lost by pressing Cancel is worse
            // than one that is kept by mistake.
            _settings.AiAuth = AiAuthKinds.Microsoft;
            _settings.AiProvider = SelectedProvider.Id;
            _settings.AiAppId = appId;
            _settings.AiTenant = _aiTenant.Text.Trim();
            _settings.RememberMicrosoft(session);
            _settings.Save();

            ShowMicrosoftSignIn();

            _aiResult.ForeColor = Theme.Good;
            _aiResult.Text = "Signed in. Test the connection to prove it works.";
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 200);
        }
    }

    private void SignOutMicrosoft()
    {
        _settings.ForgetMicrosoft();
        _settings.Save();
        ShowMicrosoftSignIn();

        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Signed out. The application details are kept for next time.";
    }

    /// <summary>Offers only the routes the chosen service actually has.</summary>
    private void ReloadAuthChoices(string? wanted)
    {
        string keep = AiAuthKinds.Clean(wanted ?? SelectedAuth);

        _authKinds.Clear();
        _aiAuth.Items.Clear();

        if (SelectedProvider.Id != AiProviders.Copilot)
        {
            _authKinds.Add(AiAuthKinds.Key);
            _aiAuth.Items.Add(SelectedProvider.Id == AiProviders.Foundry
                ? "A key from the resource"
                : "A key I bought, billed by what it uses");
        }

        // The page route is offered first for Copilot, because it is the only one of the two
        // that a technician can finish without booking time with an administrator.
        if (SelectedProvider.Id == AiProviders.Copilot)
        {
            _authKinds.Add(AiAuthKinds.Browser);
            _aiAuth.Items.Add("Sign in to the Copilot page, no administrator needed");
        }

        if (SelectedProvider.Id is AiProviders.Copilot or AiProviders.Foundry)
        {
            _authKinds.Add(AiAuthKinds.Microsoft);
            _aiAuth.Items.Add("Sign in with my Microsoft work account");
        }

        // Signing in here is offered before the routes that need something installed, because
        // it is the one that asks the least of the person. Claude reaches the ordinary address
        // and is the clean case; ChatGPT and Gemini reach a consumer subscription the vendor's
        // own way, so they say advanced.
        if (SelectedProvider.Id == AiProviders.Anthropic)
        {
            _authKinds.Add(AiAuthKinds.Subscription);
            _aiAuth.Items.Add("Sign in with my Claude subscription");
        }
        else if (SelectedProvider.Id == AiProviders.OpenAi)
        {
            _authKinds.Add(AiAuthKinds.Subscription);
            _aiAuth.Items.Add("Sign in with my ChatGPT subscription, advanced");
        }
        else if (SelectedProvider.Id == AiProviders.Gemini && GeminiOAuth.Available)
        {
            _authKinds.Add(AiAuthKinds.Subscription);
            _aiAuth.Items.Add("Sign in with my Gemini plan, advanced");
        }

        AiAgent? agent = AiAgents.Find(SelectedProvider.Id);

        if (agent is not null)
        {
            _authKinds.Add(AiAuthKinds.Cli);
            _aiAuth.Items.Add($"{agent.Label} on this machine, paid by my {agent.Plan} plan");
        }

        if (SelectedProvider.Id == AiProviders.Anthropic)
        {
            _authKinds.Add(AiAuthKinds.Token);
            _aiAuth.Items.Add("A Claude subscription token, advanced");
        }

        int index = _authKinds.IndexOf(keep);
        _aiAuth.SelectedIndex = index >= 0 ? index : 0;
        ShowAuthRoute();
    }

    private string SelectedAuth =>
        _aiAuth.SelectedIndex >= 0 && _aiAuth.SelectedIndex < _authKinds.Count
            ? _authKinds[_aiAuth.SelectedIndex]
            : AiAuthKinds.Key;

    /// <summary>Shows the fields the chosen route needs and hides the rest.</summary>
    private void ShowAuthRoute()
    {
        string auth = SelectedAuth;

        _aiKeyGroup.Visible = auth == AiAuthKinds.Key;
        _aiCliGroup.Visible = auth == AiAuthKinds.Cli;
        _aiTokenGroup.Visible = auth == AiAuthKinds.Token;
        _aiSubscriptionGroup.Visible = auth == AiAuthKinds.Subscription;
        _aiBrowserGroup.Visible = auth == AiAuthKinds.Browser;
        _aiMicrosoftGroup.Visible = auth == AiAuthKinds.Microsoft;

        // A service that cannot be shown a picture should not offer to be shown one. Copilot is
        // both cases at once: its interface takes text and nothing else, and its page takes an
        // attachment the way a person would add one, so which route is chosen decides this.
        bool page = SelectedProvider.Id == AiProviders.Copilot && auth == AiAuthKinds.Browser;
        bool reads = page || SelectedProvider.SupportsPictures;

        _aiPictures.Enabled = reads;
        _aiPictureNote.Text = page
            ? "Through the page, Copilot can be shown the screenshot itself: Stepwright attaches"
              + " it to the message the way you would attach a picture yourself. That is what lets"
              + " it name what is really on screen. It is slower than sending words alone, because"
              + " every step carries a picture up before the question goes."
            : reads
                ? "This is what makes the steps genuinely good, because the picture shows what a"
                  + " browser or an application never reports. The picture for each step is sent to"
                  + " the service chosen above, and nowhere else."
                : SelectedProvider.Label + " takes text and nothing else. With this on, Stepwright"
                  + " reads the words off each screenshot here on this machine and sends only those"
                  + " words, so the assistant can still name the control that was used. No picture"
                  + " leaves the machine either way.";

        AiAgent? agent = AiAgents.Find(SelectedProvider.Id);

        // Copilot does offer models, but not to name from here: the page route uses whichever one
        // is chosen inside the Copilot window, which the signed in profile remembers, and the
        // work account route does not take a model at all. So the field and its Find button are
        // off, and the note points at where the choice actually lives.
        if (SelectedProvider.Id == AiProviders.Copilot)
        {
            _aiModels.Enabled = false;
            _aiModel.Enabled = false;
            _aiModelNote.Text = SelectedAuth == AiAuthKinds.Browser
                ? "Microsoft 365 Copilot lets you choose a model in its own window. Stepwright uses"
                  + " whichever the Copilot window is set to, so choose it there."
                : "The work account route does not take a model. Copilot answers with whatever your"
                  + " licence is set to use.";
        }
        else if (auth == AiAuthKinds.Cli && agent is not null)
        {
            string? found = AiAgents.Locate(agent, _aiCliPath.Text.Trim());

            _aiCliStatus.ForeColor = found is null ? Theme.Muted : Theme.Good;
            _aiCliStatus.Text = found is null
                ? $"{agent.Label} was not found on this machine. {agent.SignIn}"
                : $"Found {agent.Label} at {found}. {agent.SignIn}";

            _aiModels.Enabled = true;
            _aiModel.Enabled = true;
            _aiModelNote.Text =
                "Leave the model empty to use whatever the app is already set to. Find models"
                + " offers the usual names.";
        }
        else
        {
            _aiModels.Enabled = true;
            _aiModel.Enabled = true;
            _aiModelNote.Text = "Find models asks the service which ones your key is allowed to use.";
        }

        // Claude's routes reach the ordinary Anthropic address; the ChatGPT and Gemini
        // subscription routes reach their own backends and ignore this box entirely.
        if (auth == AiAuthKinds.Token
            || (auth == AiAuthKinds.Subscription && SelectedProvider.Id == AiProviders.Anthropic))
        {
            _aiBaseUrl.Text = AiProviders.Find(AiProviders.Anthropic).BaseUrl;
        }

        if (auth == AiAuthKinds.Subscription)
        {
            ShowSubscriptionSignIn();
        }
    }

    private async Task CheckAgentAsync()
    {
        AiAgent? agent = AiAgents.Find(SelectedProvider.Id);

        if (agent is null)
        {
            return;
        }

        _aiCliStatus.ForeColor = Theme.Muted;
        _aiCliStatus.Text = "Looking for " + agent.Label + "...";

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            string version = await AiAgents
                .VersionAsync(agent, _aiCliPath.Text.Trim(), cancel.Token)
                .ConfigureAwait(true);

            _aiCliStatus.ForeColor = Theme.Good;
            _aiCliStatus.Text = $"{agent.Label} answered: {StepwrightText.Shorten(version, 90)}. {agent.SignIn}";
        }
        catch (Exception error)
        {
            _aiCliStatus.ForeColor = Theme.Record;
            _aiCliStatus.Text = StepwrightText.Shorten(error.Message, 200);
        }
    }

    private void OpenAgentPage()
    {
        AiAgent? agent = AiAgents.Find(SelectedProvider.Id);

        if (agent is null)
        {
            return;
        }

        Open(agent.InstallPage);
    }

    private TabPage BuildFormatTab()
    {
        (TabPage page, TableLayoutPanel table) = NewPage("Format");

        AddNote(
            table,
            "A format decides how a guide is written out: the typeface, the sizes, whether the"
            + " styling travels on each element, and how pictures are carried. Every export and"
            + " every publish uses one, and a format is a small file you can share.");

        _exportFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        ReloadFormats(_settings.ExportFormat);
        _exportFormat.SelectedIndexChanged += (_, _) => ShowFormatDetail();

        AddField(table, "Format used when exporting", _exportFormat);

        _formatDetail.AutoSize = false;
        _formatDetail.Height = 58;
        _formatDetail.Dock = DockStyle.Fill;
        _formatDetail.ForeColor = Theme.Muted;
        _formatDetail.Font = Theme.UiSmall;
        _formatDetail.Margin = new Padding(1, 0, 0, 10);
        table.Controls.Add(_formatDetail);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = Color.Transparent,
            WrapContents = true,
        };

        buttons.Controls.Add(Action("Import a format", ImportFormat));
        buttons.Controls.Add(Action("Export this one", ExportFormat));
        buttons.Controls.Add(Action("Duplicate and edit", DuplicateFormat));
        buttons.Controls.Add(Action("Open the folder", OpenFormatFolder));
        buttons.Controls.Add(Action("Delete", DeleteFormat));

        table.Controls.Add(buttons);

        AddNote(
            table,
            "The four that ship with the app cannot be deleted. Duplicate one to make your own,"
            + " which opens the file in your editor. It is plain text, so a format can be kept"
            + " beside your other configuration and handed to someone else.");

        ShowFormatDetail();
        return page;
    }

    private TabPage BuildPublishingTab()
    {
        (TabPage page, TableLayoutPanel table) = NewPage("Publishing");

        AddNote(
            table,
            "Sends a finished guide straight into a knowledge base, with no file in between."
            + " Every secret here is encrypted for this Windows account.");

        AddHeading(table, "Hudu");

        _huduUrl.Text = _settings.HuduBaseUrl;
        AddField(table, "Address of your site, for example https://help.yourcompany.com", _huduUrl);

        _huduPublish.DropDownStyle = ComboBoxStyle.DropDownList;
        _huduPublish.Items.Add("An API key, reliable");
        _huduPublish.Items.Add("The Hudu web page, no key, advanced");
        _huduPublish.SelectedIndex = _settings.HuduUsesWeb ? 1 : 0;
        _huduPublish.SelectedIndexChanged += (_, _) => ShowHuduRoute();
        AddField(table, "How it publishes", _huduPublish);

        _huduKey.UseSystemPasswordChar = true;
        _huduKey.Text = string.IsNullOrEmpty(_settings.HuduKeyProtected) ? string.Empty : new string('*', 24);
        _huduKey.TextChanged += (_, _) => _huduKeyEdited = true;
        AddField(table, "API key, from Admin then API in Hudu", _huduKey);

        AddNote(
            table,
            "A Hudu key reaches every company on the instance, not only the one you are working"
            + " on, so treat it as you would an administrator password. The company a guide goes"
            + " to is the one chosen on the publishing window, and it is named there before"
            + " anything is sent.");

        _huduWebNote.AutoSize = true;
        _huduWebNote.MaximumSize = new Size(580, 0);
        _huduWebNote.ForeColor = Theme.Muted;
        _huduWebNote.Font = Theme.UiSmall;
        _huduWebNote.Margin = new Padding(1, 0, 0, 8);
        _huduWebNote.BackColor = Color.Transparent;
        _huduWebNote.Text =
            "The web page route needs no key. Sign in to Hudu once with the button below, and when"
            + " you publish, Stepwright opens Hudu, waits for you to start a new article in the"
            + " company you want, fills in the title and the guide, and leaves you to look it over"
            + " and press Save. It is the more fragile of the two and depends on the Hudu page"
            + " staying roughly as it is, so it is for a technician who cannot mint a key.";
        table.Controls.Add(_huduWebNote);

        var huduRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        huduRow.Controls.Add(Action("Sign in to Hudu", async () => await SignInHuduAsync().ConfigureAwait(true)));
        huduRow.Controls.Add(Action("Test the connection", async () => await TestHuduAsync().ConfigureAwait(true)));
        huduRow.Controls.Add(Action("Open the key page", OpenHuduKeys));
        table.Controls.Add(huduRow);

        _huduResult.AutoSize = false;
        _huduResult.Height = 34;
        _huduResult.Dock = DockStyle.Fill;
        _huduResult.ForeColor = Theme.Muted;
        _huduResult.Margin = new Padding(0, 6, 0, 4);
        table.Controls.Add(_huduResult);

        ShowHuduRoute();

        AddHeading(table, "Confluence");

        _confluenceAuth.DropDownStyle = ComboBoxStyle.DropDownList;
        _confluenceAuth.Items.Add("An email address and an API token");
        _confluenceAuth.Items.Add("Sign in through the browser, with your own Atlassian application");
        _confluenceAuth.SelectedIndex = _settings.ConfluenceUsesOAuth ? 1 : 0;
        _confluenceAuth.SelectedIndexChanged += (_, _) => ShowConfluenceRoute();
        AddField(table, "How Stepwright signs in", _confluenceAuth);

        _confluenceSite.Text = _settings.ConfluenceSite;
        AddField(table, "Address of your site, for example https://yourcompany.atlassian.net", _confluenceSite);

        _confluenceEmail.Text = _settings.ConfluenceEmail;
        AddField(_confluenceTokenGroup, "The email address you sign in with", _confluenceEmail);

        _confluenceToken.UseSystemPasswordChar = true;
        _confluenceToken.Text = string.IsNullOrEmpty(_settings.ConfluenceTokenProtected)
            ? string.Empty
            : new string('*', 24);

        _confluenceToken.TextChanged += (_, _) => _confluenceTokenEdited = true;
        AddField(_confluenceTokenGroup, "API token, from your Atlassian account security page", _confluenceToken);

        var atlassianRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        atlassianRow.Controls.Add(Action("Open the token page", () => Open(AtlassianTokenPage)));
        _confluenceTokenGroup.Controls.Add(atlassianRow);
        table.Controls.Add(_confluenceTokenGroup);

        BuildConfluenceOAuthGroup();
        table.Controls.Add(_confluenceOAuthGroup);

        var confluenceTest = Action("Test the connection", async () => await TestConfluenceAsync().ConfigureAwait(true));
        table.Controls.Add(confluenceTest);

        _confluenceResult.AutoSize = false;
        _confluenceResult.Height = 34;
        _confluenceResult.Dock = DockStyle.Fill;
        _confluenceResult.ForeColor = Theme.Muted;
        _confluenceResult.Margin = new Padding(0, 6, 0, 4);
        table.Controls.Add(_confluenceResult);

        return page;
    }

    /// <summary>
    /// Signing in through the browser needs an application registered with Atlassian, because
    /// Atlassian issues these tokens to a named application rather than to a person. That is a
    /// once only job in their developer console, and the values it gives you go here.
    /// </summary>
    private void BuildConfluenceOAuthGroup()
    {
        if (Connect.HasBroker)
        {
            AddNote(
                _confluenceOAuthGroup,
                "Press sign in, approve it in the browser, and you are finished. There is nothing"
                + " to register and nothing to paste: this copy of Stepwright already has an"
                + " application, and the sign in goes to Atlassian exactly as it always did.");
        }
        else
        {
            AddNote(
                _confluenceOAuthGroup,
                "Register an application once in the Atlassian developer console, give it the"
                + " Confluence permissions, and add " + AtlassianOAuth.CallbackUrl
                + " as its callback address. Then sign in here and nothing has to be pasted again.");

            _confluenceClientId.Text = _settings.ConfluenceClientId;
            AddField(_confluenceOAuthGroup, "Application identifier", _confluenceClientId);

            _confluenceSecret.UseSystemPasswordChar = true;
            _confluenceSecret.Text = string.IsNullOrEmpty(_settings.ConfluenceClientSecretProtected)
                ? string.Empty
                : new string('*', 24);

            _confluenceSecret.TextChanged += (_, _) => _confluenceSecretEdited = true;
            AddField(_confluenceOAuthGroup, "Application secret", _confluenceSecret);
        }

        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            WrapContents = false,
        };

        row.Controls.Add(Action("Sign in to Atlassian", async () => await SignInConfluenceAsync().ConfigureAwait(true)));
        row.Controls.Add(Action("Sign out", SignOutConfluence));
        row.Controls.Add(Action("Open the console", () => Open(AtlassianOAuth.ConsolePage)));

        _confluenceOAuthGroup.Controls.Add(row);

        _confluenceSignedIn.AutoSize = true;
        _confluenceSignedIn.MaximumSize = new Size(580, 0);
        _confluenceSignedIn.ForeColor = Theme.Muted;
        _confluenceSignedIn.Font = Theme.UiSmall;
        _confluenceSignedIn.Margin = new Padding(1, 0, 0, 10);
        _confluenceSignedIn.BackColor = Color.Transparent;
        _confluenceOAuthGroup.Controls.Add(_confluenceSignedIn);

        ShowConfluenceSignIn();
    }

    private void ShowConfluenceRoute()
    {
        bool oauth = _confluenceAuth.SelectedIndex == 1;
        _confluenceTokenGroup.Visible = !oauth;
        _confluenceOAuthGroup.Visible = oauth;
    }

    private void ShowConfluenceSignIn()
    {
        _confluenceSignedIn.ForeColor = _settings.HasConfluenceSignIn ? Theme.Good : Theme.Muted;
        _confluenceSignedIn.Text = _settings.HasConfluenceSignIn
            ? $"Signed in to {_settings.ConfluenceSiteName} at {_settings.ConfluenceSite}."
              + " Everything published goes there until you sign in somewhere else."
            : "Not signed in yet.";
    }

    private async Task SignInConfluenceAsync()
    {
        _confluenceResult.ForeColor = Theme.Muted;
        _confluenceResult.Text = "Opening the browser...";

        try
        {
            string secret = _confluenceSecretEdited
                ? _confluenceSecret.Text.Trim()
                : _settings.GetConfluenceSecret();

            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));

            bool own = _confluenceClientId.Text.Trim().Length > 0;

            AtlassianSession session = Connect.HasBroker && !own
                ? await AtlassianOAuth
                    .SignInThroughBrokerAsync(
                        message => _confluenceResult.Text = message,
                        cancel.Token)
                    .ConfigureAwait(true)
                : await AtlassianOAuth
                    .SignInAsync(
                        _confluenceClientId.Text.Trim(),
                        secret,
                        message => _confluenceResult.Text = message,
                        cancel.Token)
                    .ConfigureAwait(true);

            // A person who supports several customers is signed in to several Confluence sites,
            // and guessing which one they meant would publish one customer's work into another
            // customer's space. So they are asked, every time there is a choice to make.
            if (session.Sites.Count > 1)
            {
                AtlassianSite? picked = ChooseSite(session.Sites);

                if (picked is null)
                {
                    _confluenceResult.ForeColor = Theme.Muted;
                    _confluenceResult.Text = "Signed in, but no site was chosen, so nothing was saved.";
                    return;
                }

                session = new AtlassianSession
                {
                    AccessToken = session.AccessToken,
                    RefreshToken = session.RefreshToken,
                    Expires = session.Expires,
                    CloudId = picked.CloudId,
                    SiteUrl = picked.Url,
                    SiteName = picked.Name,
                    Sites = session.Sites,
                };
            }

            // Saved straight away, because a sign in that is lost by pressing Cancel is worse
            // than one that is kept by mistake.
            _settings.ConfluenceAuth = "oauth";
            _settings.ConfluenceClientId = _confluenceClientId.Text.Trim();

            if (_confluenceSecretEdited)
            {
                _settings.SetConfluenceSecret(secret);
            }

            _settings.RememberConfluence(session);
            _settings.Save();

            _confluenceSite.Text = _settings.ConfluenceSite;
            ShowConfluenceSignIn();

            _confluenceResult.ForeColor = Theme.Good;
            _confluenceResult.Text = "Signed in to " + session.SiteName + ".";
        }
        catch (Exception error)
        {
            _confluenceResult.ForeColor = Theme.Record;
            _confluenceResult.Text = StepwrightText.Shorten(error.Message, 200);
        }
    }

    /// <summary>
    /// Asks which Confluence site the person meant. Deliberately a blocking question with no
    /// default: the wrong answer here writes one customer's documentation into another
    /// customer's site, which is not the kind of mistake a guess should be allowed to make.
    /// </summary>
    private AtlassianSite? ChooseSite(IReadOnlyList<AtlassianSite> sites)
    {
        using var dialog = new Form
        {
            Text = "Which Confluence site?",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(460, 230),
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Theme.Window,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
        };

        var caption = new Label
        {
            Text = "This sign in covers more than one site. Choose the one to publish into.",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 40,
            ForeColor = Theme.Muted,
            Padding = new Padding(2, 6, 2, 0),
        };

        var list = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            BackColor = Theme.Panel,
            ForeColor = Theme.Text,
        };

        foreach (AtlassianSite site in sites)
        {
            list.Items.Add(site);
        }

        list.SelectedIndex = 0;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 46,
            Padding = new Padding(0, 8, 0, 0),
            BackColor = Color.Transparent,
        };

        var choose = new Button { Text = "Use this site", AutoSize = true, MinimumSize = new Size(110, 30) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 30) };

        Theme.StyleButton(choose, primary: true);
        Theme.StyleButton(cancel);

        choose.Click += (_, _) => dialog.DialogResult = DialogResult.OK;
        cancel.Click += (_, _) => dialog.DialogResult = DialogResult.Cancel;
        list.DoubleClick += (_, _) => dialog.DialogResult = DialogResult.OK;

        buttons.Controls.Add(choose);
        buttons.Controls.Add(cancel);

        dialog.Controls.Add(list);
        dialog.Controls.Add(caption);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = choose;
        dialog.CancelButton = cancel;

        Theme.Apply(dialog);

        return dialog.ShowDialog(this) == DialogResult.OK && list.SelectedItem is AtlassianSite chosen
            ? chosen
            : null;
    }

    private void SignOutConfluence()
    {
        _settings.ForgetConfluence();
        _settings.Save();
        ShowConfluenceSignIn();

        _confluenceResult.ForeColor = Theme.Muted;
        _confluenceResult.Text = "Signed out. The application details are kept for next time.";
    }

    private Button Action(string text, Action work)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(120, 30),
            Margin = new Padding(0, 2, 8, 6),
        };

        Theme.StyleButton(button);
        button.Click += (_, _) => work();
        return button;
    }

    // ------------------------------------------------------------------ formats

    private void ReloadFormats(string? chosen)
    {
        _exportFormat.Items.Clear();

        foreach (FormatProfile profile in FormatProfiles.All())
        {
            _exportFormat.Items.Add(profile.Name);
        }

        int index = chosen is null ? -1 : _exportFormat.Items.IndexOf(chosen);
        _exportFormat.SelectedIndex = index >= 0 ? index : 0;
    }

    private FormatProfile ChosenFormat() => FormatProfiles.Find(_exportFormat.SelectedItem as string);

    private void ShowFormatDetail()
    {
        FormatProfile profile = ChosenFormat();
        string kind = profile.IsBuiltIn ? "Ships with the app" : "Yours, saved on this machine";
        _formatDetail.Text = profile.Description + Environment.NewLine + kind;
    }

    private void ImportFormat()
    {
        using var dialog = new OpenFileDialog { Filter = FormatProfiles.FileFilter };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        FormatProfile? loaded = FormatProfiles.Load(dialog.FileName);

        if (loaded is null)
        {
            MessageBox.Show(this, "That file is not a format Stepwright understands.", "Stepwright");
            return;
        }

        FormatProfiles.Save(loaded);
        ReloadFormats(loaded.Name);
        ShowFormatDetail();
    }

    private void ExportFormat()
    {
        FormatProfile profile = ChosenFormat();

        using var dialog = new SaveFileDialog
        {
            Filter = FormatProfiles.FileFilter,
            FileName = profile.Name + FormatProfiles.FileExtension,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            FormatProfiles.Export(profile, dialog.FileName);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, "The format could not be written. " + error.Message, "Stepwright");
        }
    }

    private void DuplicateFormat()
    {
        FormatProfile copy = ChosenFormat().Copy();
        copy.Name = copy.Name + " copy";
        copy.Description = "Your own version.";

        FormatProfiles.Save(copy);
        ReloadFormats(copy.Name);
        ShowFormatDetail();
        OpenFormatFolder();
    }

    private void OpenFormatFolder()
    {
        try
        {
            Directory.CreateDirectory(FormatProfiles.Folder);

            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = FormatProfiles.Folder;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // Nothing sensible to do when the folder cannot be shown.
        }
    }

    private void DeleteFormat()
    {
        FormatProfile profile = ChosenFormat();

        if (profile.IsBuiltIn)
        {
            MessageBox.Show(this, "The formats that ship with the app cannot be deleted.", "Stepwright");
            return;
        }

        if (MessageBox.Show(
                this,
                $"Delete the format {profile.Name}?",
                "Stepwright",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        FormatProfiles.Delete(profile.Name);
        ReloadFormats(null);
        ShowFormatDetail();
    }

    // ------------------------------------------------------------------ publishing

    /// <summary>
    /// Signing in to Hudu in a window, and taking the key from the page rather than asking the
    /// person to carry it across. Hudu only mints keys for administrators, and nothing here can
    /// change that, but everything else about the setup goes away.
    /// </summary>
    /// <summary>Shows the key field or the web note depending on how Hudu is set to publish.</summary>
    private void ShowHuduRoute()
    {
        bool web = _huduPublish.SelectedIndex == 1;

        _huduKey.Enabled = !web;
        _huduWebNote.Visible = web;
    }

    private async Task SignInHuduAsync()
    {
        if (!WebSession.Available)
        {
            _huduResult.ForeColor = Theme.Record;
            _huduResult.Text = WebSession.Missing;
            return;
        }

        try
        {
            _huduResult.ForeColor = Theme.Muted;
            _huduResult.Text = "Opening Hudu...";

            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(30));

            string found = await HuduWeb
                .SignInAsync(this, _huduUrl.Text.Trim(), cancel.Token)
                .ConfigureAwait(true);

            if (found.Length == 0)
            {
                _huduResult.ForeColor = Theme.Muted;
                _huduResult.Text =
                    "No key was on the page when the window closed. Create one in Hudu under"
                    + " Admin then API, leave it on screen, and press Sign in to Hudu again.";
                return;
            }

            _huduKey.Text = found;
            _huduKeyEdited = true;

            _huduResult.ForeColor = Theme.Good;
            _huduResult.Text = "Key taken from the page. Test the connection to prove it works.";
        }
        catch (Exception error)
        {
            _huduResult.ForeColor = Theme.Record;
            _huduResult.Text = StepwrightText.Shorten(error.Message, 220);
        }
    }

    private async Task TestHuduAsync()
    {
        _huduResult.ForeColor = Theme.Muted;
        _huduResult.Text = "Talking to Hudu...";

        try
        {
            string key = _huduKeyEdited ? _huduKey.Text.Trim() : _settings.GetHuduKey();
            var client = new Publish.HuduClient(_huduUrl.Text.Trim(), key);

            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            string message = await client.CheckAsync(cancel.Token).ConfigureAwait(true);

            _huduResult.ForeColor = Theme.Good;
            _huduResult.Text = message;
        }
        catch (Exception error)
        {
            _huduResult.ForeColor = Theme.Record;
            _huduResult.Text = StepwrightText.Shorten(error.Message, 180);
        }
    }

    private async Task TestConfluenceAsync()
    {
        _confluenceResult.ForeColor = Theme.Muted;
        _confluenceResult.Text = "Talking to Confluence...";

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));

            // Tested against what is on screen, so a value typed but not yet saved is the one
            // being proved.
            var probe = new AppSettings
            {
                ConfluenceAuth = _confluenceAuth.SelectedIndex == 1 ? "oauth" : "token",
                ConfluenceSite = _confluenceSite.Text.Trim(),
                ConfluenceEmail = _confluenceEmail.Text.Trim(),
                ConfluenceTokenProtected = _settings.ConfluenceTokenProtected,
                ConfluenceClientId = _confluenceClientId.Text.Trim(),
                ConfluenceClientSecretProtected = _settings.ConfluenceClientSecretProtected,
                ConfluenceRefreshProtected = _settings.ConfluenceRefreshProtected,
                ConfluenceAccessProtected = _settings.ConfluenceAccessProtected,
                ConfluenceAccessExpires = _settings.ConfluenceAccessExpires,
                ConfluenceCloudId = _settings.ConfluenceCloudId,
            };

            if (_confluenceTokenEdited)
            {
                probe.SetConfluenceToken(_confluenceToken.Text.Trim());
            }

            if (_confluenceSecretEdited)
            {
                probe.SetConfluenceSecret(_confluenceSecret.Text.Trim());
            }

            Publish.ConfluenceClient client = await Publish.ConfluenceClient
                .CreateAsync(probe, cancel.Token)
                .ConfigureAwait(true);

            string message = await client.CheckAsync(cancel.Token).ConfigureAwait(true);

            _confluenceResult.ForeColor = Theme.Good;
            _confluenceResult.Text = message;
        }
        catch (Exception error)
        {
            _confluenceResult.ForeColor = Theme.Record;
            _confluenceResult.Text = StepwrightText.Shorten(error.Message, 180);
        }
    }

    // ------------------------------------------------------------------ assistant actions

    private AiProvider SelectedProvider =>
        AiProviders.All[Math.Clamp(_aiProvider.SelectedIndex, 0, AiProviders.All.Count - 1)];

    /// <summary>Fills the address and the model with values that work for the chosen service.</summary>
    private void ApplyProviderPreset()
    {
        AiProvider provider = SelectedProvider;
        _aiBaseUrl.Text = provider.BaseUrl;
        _aiModel.Items.Clear();
        _aiModel.Text = provider.Model;
        ReloadAuthChoices(SelectedAuth);
        ShowProviderHint();
    }

    private void ShowProviderHint()
    {
        AiProvider provider = SelectedProvider;
        _aiHint.Text = provider.Hint;
        _aiKeyLink.Visible = !string.IsNullOrEmpty(provider.KeyPage);
    }

    private void OpenKeyPage() => Open(SelectedProvider.KeyPage);

    private const string AtlassianTokenPage = "https://id.atlassian.com/manage-profile/security/api-tokens";

    /// <summary>
    /// Hudu keeps its keys under the admin area of your own site, so the address is built from
    /// the one already filled in rather than being somewhere on the internet.
    /// </summary>
    private void OpenHuduKeys()
    {
        string site = _huduUrl.Text.Trim().TrimEnd('/');

        if (site.Length == 0)
        {
            MessageBox.Show(
                this,
                "Fill in the address of your Hudu site first, then this opens the page where its keys live.",
                "Stepwright",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            return;
        }

        if (!site.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            site = "https://" + site;
        }

        Open(site + "/admin/api_keys");
    }

    private static void Open(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = address;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // No browser association, nothing sensible to do.
        }
    }

    /// <summary>
    /// Opens a console window with the command already running, and leaves it open afterwards.
    /// These sign ins open a browser themselves and then print something worth reading, so the
    /// window has to stay rather than flash past.
    /// </summary>
    private void RunInConsole(string command, string what)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            process.StartInfo.Arguments = "/k " + command;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch (Exception error)
        {
            MessageBox.Show(
                this,
                $"A window could not be opened for {command}. Run it yourself in a terminal. {error.Message}",
                "Stepwright",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            return;
        }

        MessageBox.Show(
            this,
            what,
            "Stepwright",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    /// <summary>The values the buttons on this page should be tried against.</summary>
    private AppSettings Probe()
    {
        var probe = new AppSettings
        {
            AiProvider = SelectedProvider.Id,
            AiAuth = SelectedAuth,
            AiBaseUrl = _aiBaseUrl.Text.Trim(),
            AiModel = _aiModel.Text.Trim(),
            AiCliPath = _aiCliPath.Text.Trim(),
            AiKeyProtected = _settings.AiKeyProtected,
            AiTokenProtected = _settings.AiTokenProtected,
            AiAppId = _aiAppId.Text.Trim(),
            AiTenant = _aiTenant.Text.Trim(),
            AiRefreshProtected = _settings.AiRefreshProtected,
            AiAccessProtected = _settings.AiAccessProtected,
            AiAccessExpires = _settings.AiAccessExpires,
        };

        if (_keyEdited && !string.IsNullOrWhiteSpace(_aiKey.Text))
        {
            probe.SetAiKey(_aiKey.Text.Trim());
        }

        if (_tokenEdited && !string.IsNullOrWhiteSpace(_aiToken.Text))
        {
            probe.SetAiToken(_aiToken.Text.Trim());
        }

        return probe;
    }

    private async Task LoadModelsAsync()
    {
        _aiModels.Enabled = false;
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Asking the service which models it has...";

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            IReadOnlyList<string> models = await AiClient.ListModelsAsync(Probe(), cancel.Token).ConfigureAwait(true);

            if (models.Count == 0)
            {
                _aiResult.ForeColor = Theme.Muted;
                _aiResult.Text = "The service returned no models. Type the name in yourself.";
                return;
            }

            string current = _aiModel.Text;
            _aiModel.Items.Clear();
            foreach (string model in models)
            {
                _aiModel.Items.Add(model);
            }

            // Keep whatever was chosen before when the service still offers it.
            int match = models.ToList().FindIndex(m => string.Equals(m, current, StringComparison.OrdinalIgnoreCase));
            if (match >= 0)
            {
                _aiModel.SelectedIndex = match;
            }
            else
            {
                _aiModel.Text = current;
            }

            _aiResult.ForeColor = Theme.Good;
            _aiResult.Text = $"Found {models.Count} models. Open the list to choose one.";
            _aiModel.DroppedDown = true;
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 180);
        }
        finally
        {
            _aiModels.Enabled = true;
        }
    }

    private async Task TestAsync()
    {
        _aiTest.Enabled = false;
        _aiResult.ForeColor = Theme.Muted;
        _aiResult.Text = "Talking to the service...";

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            string reply = await AiPolisher.TestAsync(Probe(), cancel.Token).ConfigureAwait(true);
            _aiResult.ForeColor = Theme.Good;
            _aiResult.Text = "Connected. The model said: " + StepwrightText.Shorten(reply, 80);
        }
        catch (Exception error)
        {
            _aiResult.ForeColor = Theme.Record;
            _aiResult.Text = StepwrightText.Shorten(error.Message, 180);
        }
        finally
        {
            _aiTest.Enabled = true;
        }
    }

    private void Commit()
    {
        _settings.Author = _author.Text.Trim();

        // A key box that is under policy holds a sentence saying so, not a key, and a disabled box
        // cannot have been edited. Guarding here as well means a policy that arrives while the
        // window is open still cannot be written over by a save.
        if (!string.IsNullOrWhiteSpace(Policy.Current.AiKeyProtected)) { _keyEdited = false; }
        if (!string.IsNullOrWhiteSpace(Policy.Current.HuduKeyProtected)) { _huduKeyEdited = false; }
        if (!string.IsNullOrWhiteSpace(Policy.Current.ConfluenceToken())) { _confluenceTokenEdited = false; }
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
        _settings.GifMotion = _gifMotion.SelectedItem as string ?? "Normal";
        _settings.GifWidth = (int)_gifWidth.Value;

        _settings.HotkeyStartPause = KeyOf(_keyStart);
        _settings.HotkeyStop = KeyOf(_keyStop);
        _settings.HotkeyShot = KeyOf(_keyShot);
        _settings.HotkeyNeedsModifiers = _keyModifiers.Checked;

        _settings.AiEnabled = _aiEnabled.Checked;
        _settings.AiProvider = SelectedProvider.Id;
        _settings.AiAuth = SelectedAuth;
        _settings.AiBaseUrl = _aiBaseUrl.Text.Trim();
        _settings.AiModel = _aiModel.Text.Trim();
        _settings.AiCliPath = _aiCliPath.Text.Trim();
        _settings.AiAppId = _aiAppId.Text.Trim();
        _settings.AiTenant = _aiTenant.Text.Trim();
        _settings.AiSendScreenshots = _aiPictures.Checked;
        _settings.AiWriteNotes = _aiNotes.Checked;

        if (_keyEdited)
        {
            _settings.SetAiKey(_aiKey.Text.Trim());
        }

        if (_tokenEdited)
        {
            _settings.SetAiToken(_aiToken.Text.Trim());
        }

        _settings.ExportFormat = _exportFormat.SelectedItem as string ?? "Stepwright";

        _settings.HuduBaseUrl = _huduUrl.Text.Trim();
        _settings.HuduPublish = _huduPublish.SelectedIndex == 1 ? "web" : "key";
        if (_huduKeyEdited)
        {
            _settings.SetHuduKey(_huduKey.Text.Trim());
        }

        _settings.ConfluenceAuth = _confluenceAuth.SelectedIndex == 1 ? "oauth" : "token";
        _settings.ConfluenceSite = _confluenceSite.Text.Trim();
        _settings.ConfluenceEmail = _confluenceEmail.Text.Trim();
        _settings.ConfluenceClientId = _confluenceClientId.Text.Trim();

        if (_confluenceTokenEdited)
        {
            _settings.SetConfluenceToken(_confluenceToken.Text.Trim());
        }

        if (_confluenceSecretEdited)
        {
            _settings.SetConfluenceSecret(_confluenceSecret.Text.Trim());
        }

        _settings.Save();
        DialogResult = DialogResult.OK;
    }

    // ------------------------------------------------------------------ layout helpers

    private static (TabPage Page, TableLayoutPanel Table) NewPage(string title)
    {
        var page = new TabPage(title)
        {
            BackColor = Theme.Panel,
            Padding = new Padding(18, 14, 22, 14),
            AutoScroll = true,
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(table);
        return (page, table);
    }

    /// <summary>A block of fields that can be shown or hidden as one.</summary>
    private static TableLayoutPanel Group()
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = Color.Transparent,
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Theme.Muted,
        Font = Theme.UiSmall,
        Margin = new Padding(1, 4, 0, 0),
        BackColor = Color.Transparent,
    };

    private static void AddField(TableLayoutPanel table, string label, Control input, bool narrow = false)
    {
        table.Controls.Add(Caption(label));

        if (!narrow)
        {
            input.Dock = DockStyle.Fill;
        }

        input.Margin = new Padding(0, 2, 0, 12);
        table.Controls.Add(input);
    }

    private static void AddCheck(TableLayoutPanel table, CheckBox box, string label, bool value)
    {
        box.Text = label;
        box.Checked = value;
        box.AutoSize = true;
        box.Margin = new Padding(0, 4, 0, 4);
        box.BackColor = Color.Transparent;
        table.Controls.Add(box);
    }

    private static void AddNote(TableLayoutPanel table, string text)
    {
        table.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(580, 0),
            ForeColor = Theme.Muted,
            Font = Theme.UiSmall,
            Margin = new Padding(1, 2, 0, 12),
            BackColor = Color.Transparent,
        });
    }

    private static void AddHeading(TableLayoutPanel table, string text)
    {
        table.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = Theme.UiBold,
            ForeColor = Theme.Text,
            Margin = new Padding(0, 14, 0, 6),
            BackColor = Color.Transparent,
        });
    }

    private static Control KeyCell(string label, ComboBox combo)
    {
        var cell = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 12, 12),
            BackColor = Color.Transparent,
        };

        cell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        combo.Dock = DockStyle.Fill;
        combo.Margin = new Padding(0, 2, 0, 0);

        cell.Controls.Add(Caption(label));
        cell.Controls.Add(combo);
        return cell;
    }

    private static void Configure(NumericUpDown control, int min, int max, int value, int step = 1)
    {
        control.Minimum = min;
        control.Maximum = max;
        control.Increment = step;
        control.Width = 120;
        control.Value = Math.Clamp(value, min, max);
    }

    private static void FillKeys(ComboBox combo, int selected)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        for (int key = 0x70; key <= 0x7B; key++)
        {
            combo.Items.Add("F" + (key - 0x6F));
        }

        combo.SelectedIndex = Math.Clamp(selected - 0x70, 0, combo.Items.Count - 1);
    }

    private static int KeyOf(ComboBox combo) => 0x70 + Math.Max(0, combo.SelectedIndex);
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
