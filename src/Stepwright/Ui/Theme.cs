using System.Drawing.Drawing2D;

namespace Stepwright.Ui;

/// <summary>One place for every colour and for the small amount of control painting the app does.</summary>
public static class Theme
{
    public static bool Dark { get; private set; } = true;

    public static Color Window { get; private set; } = Color.FromArgb(24, 25, 29);
    public static Color Panel { get; private set; } = Color.FromArgb(32, 34, 39);
    public static Color Raised { get; private set; } = Color.FromArgb(40, 43, 50);
    public static Color Border { get; private set; } = Color.FromArgb(52, 56, 64);
    public static Color Text { get; private set; } = Color.FromArgb(233, 235, 240);
    public static Color Muted { get; private set; } = Color.FromArgb(146, 152, 163);
    public static Color Accent { get; private set; } = Color.FromArgb(79, 125, 247);
    public static Color AccentText { get; private set; } = Color.White;
    public static Color Record { get; private set; } = Color.FromArgb(226, 68, 68);
    public static Color Good { get; private set; } = Color.FromArgb(64, 186, 120);

    public static Font Ui { get; } = new("Segoe UI", 9.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiBold { get; } = new("Segoe UI", 9.25f, FontStyle.Bold, GraphicsUnit.Point);
    public static Font UiSmall { get; } = new("Segoe UI", 8.25f, FontStyle.Regular, GraphicsUnit.Point);
    public static Font UiTitle { get; } = new("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point);

    public static void Use(bool dark)
    {
        Dark = dark;
        if (dark)
        {
            Window = Color.FromArgb(24, 25, 29);
            Panel = Color.FromArgb(32, 34, 39);
            Raised = Color.FromArgb(40, 43, 50);
            Border = Color.FromArgb(52, 56, 64);
            Text = Color.FromArgb(233, 235, 240);
            Muted = Color.FromArgb(146, 152, 163);
        }
        else
        {
            Window = Color.FromArgb(246, 247, 249);
            Panel = Color.White;
            Raised = Color.FromArgb(238, 240, 244);
            Border = Color.FromArgb(214, 218, 224);
            Text = Color.FromArgb(22, 24, 29);
            Muted = Color.FromArgb(110, 117, 128);
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
            ? ControlPaint.Light(Accent, 0.12f)
            : ControlPaint.Light(Raised, 0.25f);
        button.Font = Ui;
        button.UseVisualStyleBackColor = false;
        button.Cursor = Cursors.Hand;
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

    public static void EnableDarkTitleBar(IntPtr handle)
    {
        if (!Dark)
        {
            return;
        }

        try
        {
            int enabled = 1;
            // Attribute 20 on current builds, 19 on the first release that supported it.
            Native.NativeMethods.DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
            Native.NativeMethods.DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
            // The title bar simply stays light on older builds.
        }
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

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item?.Enabled == false ? Theme.Muted : Theme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Theme.Text;
        base.OnRenderArrow(e);
    }
}
