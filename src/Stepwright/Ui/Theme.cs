using System.Drawing.Drawing2D;

namespace Stepwright.Ui;

/// <summary>One place for every colour and for the small amount of control painting the app does.</summary>
public static class Theme
{
    public static bool Dark { get; private set; } = true;

    public static Color Window { get; private set; } = Color.FromArgb(20, 21, 25);
    public static Color Panel { get; private set; } = Color.FromArgb(27, 29, 34);
    public static Color Raised { get; private set; } = Color.FromArgb(36, 39, 46);
    public static Color Hover { get; private set; } = Color.FromArgb(45, 49, 58);
    public static Color Border { get; private set; } = Color.FromArgb(48, 52, 61);
    public static Color Text { get; private set; } = Color.FromArgb(236, 238, 243);
    public static Color Muted { get; private set; } = Color.FromArgb(142, 149, 161);
    public static Color Accent { get; private set; } = Color.FromArgb(88, 132, 255);
    public static Color AccentSoft { get; private set; } = Color.FromArgb(38, 48, 78);
    public static Color AccentText { get; private set; } = Color.White;
    public static Color Record { get; private set; } = Color.FromArgb(232, 74, 74);
    public static Color Good { get; private set; } = Color.FromArgb(70, 192, 126);

    /// <summary>Corner radius used across the window, so nothing looks sharper than the rest.</summary>
    public const int Radius = 7;

    private static string Face { get; } = PickFace();

    public static Font Ui { get; } = new(Face, 9.5f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiBold { get; } = new(Face, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
    public static Font UiSmall { get; } = new(Face, 8.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiTiny { get; } = new(Face, 7.5f, FontStyle.Bold, GraphicsUnit.Point);
    public static Font UiTitle { get; } = new(Face, 12f, FontStyle.Bold, GraphicsUnit.Point);
    public static Font UiStep { get; } = new(Face, 10f, FontStyle.Regular, GraphicsUnit.Point);

    /// <summary>Windows 11 ships a newer text face. Older builds fall back to the familiar one.</summary>
    private static string PickFace()
    {
        foreach (string candidate in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            try
            {
                using var probe = new Font(candidate, 9f);
                if (string.Equals(probe.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
            catch
            {
                // Try the next one.
            }
        }

        return "Segoe UI";
    }

    public static void Use(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Window = Color.FromArgb(20, 21, 25);
            Panel = Color.FromArgb(27, 29, 34);
            Raised = Color.FromArgb(36, 39, 46);
            Hover = Color.FromArgb(45, 49, 58);
            Border = Color.FromArgb(48, 52, 61);
            Text = Color.FromArgb(236, 238, 243);
            Muted = Color.FromArgb(142, 149, 161);
            AccentSoft = Color.FromArgb(38, 48, 78);
        }
        else
        {
            Window = Color.FromArgb(244, 246, 249);
            Panel = Color.White;
            Raised = Color.FromArgb(240, 242, 246);
            Hover = Color.FromArgb(232, 236, 242);
            Border = Color.FromArgb(219, 223, 230);
            Text = Color.FromArgb(20, 22, 27);
            Muted = Color.FromArgb(105, 112, 124);
            AccentSoft = Color.FromArgb(226, 234, 255);
        }
    }

    /// <summary>Paints a whole control tree in the current colours.</summary>
    public static void Apply(Control root)
    {
        root.BackColor = root is Form ? Window : root.BackColor;

        foreach (Control control in root.Controls)
        {
            switch (control)
            {
                case TextBox box:
                    box.BackColor = Raised;
                    box.ForeColor = Text;
                    box.BorderStyle = BorderStyle.FixedSingle;
                    break;
                case ComboBox combo:
                    combo.BackColor = Raised;
                    combo.ForeColor = Text;
                    combo.FlatStyle = FlatStyle.Flat;
                    break;
                case NumericUpDown number:
                    number.BackColor = Raised;
                    number.ForeColor = Text;
                    break;
                case CheckBox check:
                    check.ForeColor = Text;
                    check.BackColor = Color.Transparent;
                    break;
                case RadioButton radio:
                    radio.ForeColor = Text;
                    radio.BackColor = Color.Transparent;
                    break;
                case LinkLabel link:
                    link.LinkColor = Accent;
                    link.ActiveLinkColor = Accent;
                    link.BackColor = Color.Transparent;
                    break;
                case Label label:
                    label.ForeColor = label.Tag as string == "muted" ? Muted : Text;
                    label.BackColor = Color.Transparent;
                    break;
                case Button button:
                    StyleButton(button, primary: button.Tag as string == "primary");
                    break;
                case TabPage page:
                    page.BackColor = Panel;
                    page.ForeColor = Text;
                    break;
                case Panel panel when panel.Tag as string == "raised":
                    panel.BackColor = Raised;
                    break;
                case GroupBox group:
                    group.ForeColor = Text;
                    group.BackColor = Color.Transparent;
                    break;
                case ListBox list:
                    list.BackColor = Panel;
                    list.ForeColor = Text;
                    break;
                case TabControl tabs:
                    tabs.BackColor = Panel;
                    break;
                case SplitContainer split:
                    split.BackColor = Border;
                    split.Panel1.BackColor = Panel;
                    split.Panel2.BackColor = Window;
                    break;
            }

            if (control.HasChildren)
            {
                Apply(control);
            }
        }
    }

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = primary ? 0 : 1;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = primary ? Accent : Raised;
        button.ForeColor = primary ? AccentText : Text;
        button.FlatAppearance.MouseOverBackColor = primary
            ? ControlPaint.Light(Accent, 0.15f)
            : Hover;
        button.FlatAppearance.MouseDownBackColor = primary
            ? ControlPaint.Dark(Accent, 0.05f)
            : Raised;
        button.Font = Ui;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
        button.Padding = new Padding(6, 2, 6, 2);
    }

    public static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(2, Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height)));
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Asks the window manager for the modern treatment: a title bar that matches the window,
    /// rounded corners and a quiet border. Older builds ignore the attributes they do not know.
    /// </summary>
    public static void StyleWindow(IntPtr handle)
    {
        try
        {
            int dark = Dark ? 1 : 0;

            // Attribute 20 on current builds, 19 on the first release that supported it.
            Native.NativeMethods.DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            Native.NativeMethods.DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));

            int rounded = 2; // Round
            Native.NativeMethods.DwmSetWindowAttribute(handle, 33, ref rounded, sizeof(int));

            int caption = ToColorRef(Panel);
            Native.NativeMethods.DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));

            int border = ToColorRef(Border);
            Native.NativeMethods.DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));

            int text = ToColorRef(Text);
            Native.NativeMethods.DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        }
        catch
        {
            // The window simply keeps the system look.
        }
    }

    public static void EnableDarkTitleBar(IntPtr handle) => StyleWindow(handle);

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    /// <summary>Fills a rounded shape, the building block of every surface in the window.</summary>
    public static void FillRounded(Graphics graphics, Rectangle bounds, Color color, int radius = Radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = RoundedRect(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);

        graphics.SmoothingMode = previous;
    }

    public static void DrawRounded(Graphics graphics, Rectangle bounds, Color color, int radius = Radius, float width = 1f)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using GraphicsPath path = RoundedRect(bounds, radius);
        using var pen = new Pen(color, width);
        graphics.DrawPath(pen, path);

        graphics.SmoothingMode = previous;
    }
}

/// <summary>Menu and tool strip painting that matches the rest of the window.</summary>
public sealed class ThemeColors : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => Theme.Panel;
    public override Color ToolStripGradientMiddle => Theme.Panel;
    public override Color ToolStripGradientEnd => Theme.Panel;
    public override Color ToolStripBorder => Theme.Border;
    public override Color MenuStripGradientBegin => Theme.Panel;
    public override Color MenuStripGradientEnd => Theme.Panel;
    public override Color MenuItemSelected => Theme.Raised;
    public override Color MenuItemSelectedGradientBegin => Theme.Raised;
    public override Color MenuItemSelectedGradientEnd => Theme.Raised;
    public override Color MenuItemBorder => Theme.Border;
    public override Color MenuBorder => Theme.Border;
    public override Color MenuItemPressedGradientBegin => Theme.Raised;
    public override Color MenuItemPressedGradientEnd => Theme.Raised;
    public override Color ToolStripDropDownBackground => Theme.Panel;
    public override Color ImageMarginGradientBegin => Theme.Panel;
    public override Color ImageMarginGradientMiddle => Theme.Panel;
    public override Color ImageMarginGradientEnd => Theme.Panel;
    public override Color ButtonSelectedHighlight => Theme.Raised;
    public override Color ButtonSelectedBorder => Theme.Border;
    public override Color ButtonPressedHighlight => Theme.Raised;
    public override Color SeparatorDark => Theme.Border;
    public override Color SeparatorLight => Theme.Border;
    public override Color StatusStripGradientBegin => Theme.Panel;
    public override Color StatusStripGradientEnd => Theme.Panel;
}

public sealed class ThemeRenderer : ToolStripProfessionalRenderer
{
    public ThemeRenderer()
        : base(new ThemeColors())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var back = new SolidBrush(e.ToolStrip is ToolStripDropDown ? Theme.Panel : Theme.Panel);
        e.Graphics.FillRectangle(back, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is ToolStripDropDown)
        {
            Theme.DrawRounded(
                e.Graphics,
                new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                Theme.Border,
                Theme.Radius);
            return;
        }

        // A single quiet line rather than a frame, so the strips read as one surface.
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        DrawItemBackground(e);
    }

    protected override void OnRenderDropDownButtonBackground(ToolStripItemRenderEventArgs e)
    {
        DrawItemBackground(e);
    }

    protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
    {
        DrawItemBackground(e);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
        if (e.Item.Selected && e.Item.Enabled)
        {
            Theme.FillRounded(e.Graphics, bounds, Theme.Hover, 5);
        }
    }

    /// <summary>
    /// Every clickable item gets the same rounded highlight, and a checked one keeps a soft
    /// accent fill so the active tool is obvious at a glance.
    /// </summary>
    private static void DrawItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        var bounds = new Rectangle(1, 3, e.Item.Width - 2, e.Item.Height - 6);
        bool primary = e.Item.Tag as string == "primary";

        if (primary)
        {
            Color fill = e.Item.Pressed
                ? ControlPaint.Dark(Theme.Accent, 0.05f)
                : e.Item.Selected ? ControlPaint.Light(Theme.Accent, 0.12f) : Theme.Accent;
            Theme.FillRounded(e.Graphics, bounds, fill, 6);
            return;
        }

        if (e.Item is ToolStripButton { Checked: true })
        {
            Theme.FillRounded(e.Graphics, bounds, Theme.AccentSoft, 6);
            Theme.DrawRounded(e.Graphics, bounds, Theme.Accent, 6);
            return;
        }

        if (e.Item.Pressed)
        {
            Theme.FillRounded(e.Graphics, bounds, Theme.Raised, 6);
        }
        else if (e.Item.Selected && e.Item.Enabled)
        {
            Theme.FillRounded(e.Graphics, bounds, Theme.Hover, 6);
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        Rectangle bounds = e.Item?.Bounds ?? Rectangle.Empty;
        using var pen = new Pen(Theme.Border);

        if (e.Vertical)
        {
            int x = bounds.Width / 2;
            e.Graphics.DrawLine(pen, x, 7, x, bounds.Height - 7);
        }
        else
        {
            e.Graphics.DrawLine(pen, 8, bounds.Height / 2, bounds.Width - 8, bounds.Height / 2);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is null)
        {
            base.OnRenderItemText(e);
            return;
        }

        bool primary = e.Item.Tag as string == "primary";
        e.TextColor = !e.Item.Enabled
            ? Theme.Muted
            : primary ? Theme.AccentText
            : e.Item is ToolStripButton { Checked: true } ? Theme.Accent
            : Theme.Text;

        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Enabled == false ? Theme.Muted : Theme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        // A tick drawn by hand, because the system one carries a light background.
        var box = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, 14, 14);
        Theme.FillRounded(e.Graphics, box, Theme.Accent, 4);

        using var pen = new Pen(Theme.AccentText, 1.8f)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };

        System.Drawing.Drawing2D.SmoothingMode previous = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        e.Graphics.DrawLines(pen, new[]
        {
            new Point(box.Left + 3, box.Top + 7),
            new Point(box.Left + 6, box.Top + 10),
            new Point(box.Left + 11, box.Top + 4),
        });

        e.Graphics.SmoothingMode = previous;
    }

    protected override void OnRenderStatusStripSizingGrip(ToolStripRenderEventArgs e)
    {
        // Nothing to draw. The grip looks dated.
    }
}
