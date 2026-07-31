using Stepwright.Config;
using Stepwright.Export;
using Stepwright.Model;
using Stepwright.Publish;

namespace Stepwright.Ui;

public enum PublishDestination
{
    Hudu,
    Confluence,
}

/// <summary>
/// Sends the finished guide straight into a knowledge base. The two places want the document
/// written differently and the pictures handled differently, so each carries its own format
/// and the dialog only asks where it should land.
/// </summary>
public sealed class PublishForm : Form
{
    private readonly AppSettings _settings;
    private readonly Guide _guide;
    private readonly PublishDestination _destination;

    private readonly TextBox _title = new();
    private readonly ComboBox _first = new();
    private readonly ComboBox _second = new();
    private readonly ComboBox _third = new();
    private readonly ComboBox _format = new();
    private readonly Label _firstLabel = new();
    private readonly Label _secondLabel = new();
    private readonly Label _thirdLabel = new();
    private readonly Label _result = new();
    private readonly Button _send = new();
    private readonly Button _refresh = new();

    private HuduClient? _hudu;
    private ConfluenceClient? _confluence;
    private string _link = string.Empty;

    public PublishForm(AppSettings settings, Guide guide, PublishDestination destination)
    {
        _settings = settings;
        _guide = guide;
        _destination = destination;

        Text = destination == PublishDestination.Hudu ? "Send to Hudu" : "Send to Confluence";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(560, 430);
        MinimumSize = new Size(480, 400);
        BackColor = Theme.Window;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
        AutoScaleMode = AutoScaleMode.Dpi;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());

        Load += async (_, _) =>
        {
            Theme.Apply(this);
            Theme.StyleWindow(Handle);
            await LoadTargetsAsync().ConfigureAwait(true);
        };
    }

    private Control BuildBody()
    {
        var page = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Panel,
            Padding = new Padding(18, 16, 22, 12),
            AutoScroll = true,
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _title.Text = _guide.Title;
        AddField(table, "Title of the article", _title);

        _firstLabel.Text = _destination == PublishDestination.Hudu ? "Company" : "Space";
        _secondLabel.Text = _destination == PublishDestination.Hudu ? "Folder" : "File it under";
        _thirdLabel.Text = _destination == PublishDestination.Hudu ? "Article" : "Nothing to choose";

        _first.DropDownStyle = ComboBoxStyle.DropDownList;
        _second.DropDownStyle = ComboBoxStyle.DropDownList;
        _third.DropDownStyle = ComboBoxStyle.DropDownList;

        _first.SelectedIndexChanged += async (_, _) => await LoadChildrenAsync().ConfigureAwait(true);

        AddField(table, _firstLabel.Text, _first);
        AddField(table, _secondLabel.Text, _second);

        if (_destination == PublishDestination.Hudu)
        {
            AddField(table, _thirdLabel.Text, _third);
        }

        foreach (FormatProfile profile in FormatProfiles.All())
        {
            _format.Items.Add(profile.Name);
        }

        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        string wanted = _destination == PublishDestination.Hudu ? _settings.HuduFormat : _settings.ConfluenceFormat;
        int index = _format.Items.IndexOf(wanted);
        _format.SelectedIndex = index >= 0 ? index : 0;

        AddField(table, "Written using this format", _format);

        AddNote(
            table,
            _destination == PublishDestination.Hudu
                ? "Hudu keeps the pictures inside the article, so this goes across in one piece."
                : "Confluence keeps pictures as attachments, so the page is created first and each picture is attached to it afterwards.");

        _result.AutoSize = false;
        _result.Height = 46;
        _result.Dock = DockStyle.Fill;
        _result.ForeColor = Theme.Muted;
        _result.Margin = new Padding(0, 8, 0, 0);
        table.Controls.Add(_result);

        page.Controls.Add(table);
        return page;
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

        var row = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Theme.Panel,
            WrapContents = false,
        };

        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(92, 32) };
        _send.Text = "Send";
        _send.AutoSize = true;
        _send.MinimumSize = new Size(108, 32);
        _send.Tag = "primary";

        _refresh.Text = "Reload the list";
        _refresh.AutoSize = true;
        _refresh.MinimumSize = new Size(110, 32);

        Theme.StyleButton(_send, primary: true);
        Theme.StyleButton(close);
        Theme.StyleButton(_refresh);

        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _send.Click += async (_, _) => await SendAsync().ConfigureAwait(true);
        _refresh.Click += async (_, _) => await LoadTargetsAsync().ConfigureAwait(true);

        row.Controls.Add(close);
        row.Controls.Add(_send);
        row.Controls.Add(_refresh);
        footer.Controls.Add(row);

        CancelButton = close;
        return footer;
    }

    /// <summary>Hides the company and folder pickers, for the web route that has no list to fill them.</summary>
    private void HidePickers()
    {
        foreach (Control control in new Control[] { _first, _second, _third, _firstLabel, _secondLabel, _thirdLabel })
        {
            control.Visible = false;
        }
    }

    // ------------------------------------------------------------------ loading

    private async Task LoadTargetsAsync()
    {
        _send.Enabled = false;
        Say("Asking the site what is there...", Theme.Muted);

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            List<PublishTarget> targets;

            if (_destination == PublishDestination.Hudu)
            {
                // The web route has no key, so there is no list to ask for: the person picks the
                // company in the Hudu window. The pickers are hidden and the send goes straight on.
                if (_settings.HuduUsesWeb)
                {
                    if (string.IsNullOrWhiteSpace(_settings.HuduBaseUrl))
                    {
                        Say("Add your Hudu address under Settings first.", Theme.Record);
                        return;
                    }

                    if (!Web.HuduWeb.Remembered)
                    {
                        Say("Sign in to Hudu under Settings first, then come back.", Theme.Record);
                        return;
                    }

                    HidePickers();
                    _send.Enabled = true;
                    Say("Ready. You will choose where it goes in the Hudu window.", Theme.Good);
                    return;
                }

                if (!_settings.HasHudu)
                {
                    Say("Hudu is not set up yet. Add the address and a key under Settings.", Theme.Record);
                    return;
                }

                _hudu = new HuduClient(_settings.HuduBaseUrl, _settings.GetHuduKey());
                targets = await _hudu.CompaniesAsync(cancel.Token).ConfigureAwait(true);
            }
            else
            {
                if (!_settings.HasConfluence)
                {
                    Say(
                        _settings.ConfluenceUsesOAuth
                            ? "Confluence is not signed in yet. Sign in to Atlassian under Settings."
                            : "Confluence is not set up yet. Add the address, your email and a token under Settings.",
                        Theme.Record);
                    return;
                }

                _confluence = await ConfluenceClient.CreateAsync(_settings, cancel.Token).ConfigureAwait(true);

                targets = await _confluence.SpacesAsync(cancel.Token).ConfigureAwait(true);
            }

            Fill(_first, targets);
            await LoadChildrenAsync().ConfigureAwait(true);

            _send.Enabled = true;

            // Which place, by name, every time. A person who supports several customers cannot
            // tell one Confluence from another by the shape of the window.
            string place = _destination == PublishDestination.Hudu
                ? _settings.HuduBaseUrl
                : _settings.ConfluenceUsesOAuth
                    ? $"{_settings.ConfluenceSiteName} at {_settings.ConfluenceSite}"
                    : _settings.ConfluenceSite;

            Say(
                string.IsNullOrWhiteSpace(place) ? "Ready to send." : "Ready to send to " + place + ".",
                Theme.Good);
        }
        catch (Exception error)
        {
            Say(StepwrightText.Shorten(error.Message, 220), Theme.Record);
        }
    }

    private async Task LoadChildrenAsync()
    {
        string parent = Selected(_first)?.Id ?? string.Empty;

        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            if (_destination == PublishDestination.Hudu && _hudu is not null)
            {
                Fill(_second, await _hudu.FoldersAsync(parent, cancel.Token).ConfigureAwait(true));
                Fill(_third, await _hudu.ArticlesAsync(parent, cancel.Token).ConfigureAwait(true));
            }
            else if (_confluence is not null)
            {
                Fill(_second, await _confluence.PagesAsync(parent, cancel.Token).ConfigureAwait(true));
            }
        }
        catch (Exception error)
        {
            Say(StepwrightText.Shorten(error.Message, 220), Theme.Record);
        }
    }

    // ------------------------------------------------------------------ sending

    private async Task SendAsync()
    {
        string title = _title.Text.Trim();

        if (string.IsNullOrEmpty(title))
        {
            Say("The article needs a title.", Theme.Record);
            return;
        }

        _send.Enabled = false;
        Cursor = Cursors.WaitCursor;

        try
        {
            FormatProfile format = FormatProfiles.Find(_format.SelectedItem as string);
            AppSettings settings = _settings;
            Guide guide = _guide;

            // Said out loud, because a step marked as an animation quietly arriving as a still
            // picture reads as a fault in the recording rather than a choice in the format.
            int animated = guide.Steps.Count(s => s.Animate && Render.StepAnimator.CanAnimate(s));

            if (animated > 0 && !format.AllowAnimation)
            {
                Say($"{animated} animated steps go across as still pictures, because the {format.Name} format has animation switched off.", Theme.Muted);
            }

            // The document is built off the window thread, because every picture in the guide
            // is drawn to make it.
            var options = new HtmlOptions
            {
                Fragment = true,
                Format = format,
                EmbedImages = _destination == PublishDestination.Hudu,
                CollectImagesOnly = _destination == PublishDestination.Confluence,
            };

            Say("Building the document...", Theme.Muted);
            string html = await Task.Run(() => HtmlExporter.Build(guide, settings, options)).ConfigureAwait(true);

            using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            if (_destination == PublishDestination.Hudu && _settings.HuduUsesWeb)
            {
                _link = await Web.HuduWeb.PublishAsync(
                    _settings.HuduBaseUrl,
                    title,
                    html,
                    message => Say(message, Theme.Muted),
                    cancel.Token).ConfigureAwait(true);

                _settings.HuduFormat = format.Name;

                Say("Done in the window. If you saved it in Hudu, it is published.", Theme.Good);
            }
            else if (_destination == PublishDestination.Hudu && _hudu is not null)
            {
                // Named out loud, because one Hudu key reaches every company on the instance
                // and the only thing standing between customers is this list.
                Say($"Sending to {Selected(_first)?.Name ?? "Hudu"} at {_settings.HuduBaseUrl}...", Theme.Muted);

                _link = await _hudu.PublishAsync(
                    title,
                    html,
                    Selected(_first)?.Id ?? string.Empty,
                    Selected(_second)?.Id ?? string.Empty,
                    Selected(_third)?.Id ?? string.Empty,
                    cancel.Token).ConfigureAwait(true);

                _settings.HuduFormat = format.Name;
            }
            else if (_confluence is not null)
            {
                string space = Selected(_first)?.Id ?? string.Empty;

                if (string.IsNullOrEmpty(space))
                {
                    Say("Choose a space first.", Theme.Record);
                    return;
                }

                Say(
                    $"Sending to {Selected(_first)?.Name ?? "Confluence"} at {_settings.ConfluenceSite}...",
                    Theme.Muted);

                var progress = new Progress<string>(message => Say(message, Theme.Muted));

                _link = await _confluence.PublishAsync(
                    title,
                    html,
                    space,
                    Selected(_second)?.Id ?? string.Empty,
                    options.CollectedImages,
                    format.UseJpeg,
                    progress,
                    cancel.Token).ConfigureAwait(true);

                _settings.ConfluenceFormat = format.Name;
            }

            _settings.Save();

            string went = _destination == PublishDestination.Hudu
                ? $"{Selected(_first)?.Name} at {_settings.HuduBaseUrl}"
                : $"{Selected(_first)?.Name} at {_settings.ConfluenceSite}";

            Say($"Sent to {went}. {_link}", Theme.Good);

            if (MessageBox.Show(
                    this,
                    "The article is published. Open it now?",
                    "Stepwright",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Open(_link);
            }
        }
        catch (Exception error)
        {
            Say(StepwrightText.Shorten(error.Message, 260), Theme.Record);
        }
        finally
        {
            _send.Enabled = true;
            Cursor = Cursors.Default;
        }
    }

    private static void Open(string link)
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo.FileName = link;
            process.StartInfo.UseShellExecute = true;
            process.Start();
        }
        catch
        {
            // No browser association, nothing sensible to do.
        }
    }

    // ------------------------------------------------------------------ helpers

    private static void Fill(ComboBox box, List<PublishTarget> targets)
    {
        box.Items.Clear();
        foreach (PublishTarget target in targets)
        {
            box.Items.Add(target);
        }

        if (box.Items.Count > 0)
        {
            box.SelectedIndex = 0;
        }
    }

    private static PublishTarget? Selected(ComboBox box) => box.SelectedItem as PublishTarget;

    private void Say(string message, Color color)
    {
        _result.ForeColor = color;
        _result.Text = message;
    }

    private static void AddField(TableLayoutPanel table, string label, Control input)
    {
        table.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = Theme.Muted,
            Font = Theme.UiSmall,
            Margin = new Padding(1, 4, 0, 0),
            BackColor = Color.Transparent,
        });

        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 2, 0, 12);
        table.Controls.Add(input);
    }

    private static void AddNote(TableLayoutPanel table, string text)
    {
        table.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new Size(480, 0),
            ForeColor = Theme.Muted,
            Font = Theme.UiSmall,
            Margin = new Padding(1, 2, 0, 8),
            BackColor = Color.Transparent,
        });
    }
}
