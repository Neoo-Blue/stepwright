using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Automation;
using Stepwright.Native;

namespace Stepwright.Capture;

/// <summary>Everything known about the control the person just interacted with.</summary>
public sealed class ElementInfo
{
    public string Name { get; set; } = string.Empty;
    public string ControlType { get; set; } = string.Empty;
    public string AutomationId { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string WindowTitle { get; set; } = string.Empty;
    public string AppName { get; set; } = string.Empty;
    public string ParentName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public bool IsPassword { get; set; }
    public bool IsTextField { get; set; }

    /// <summary>Bounds in physical screen pixels. Empty when the platform gave nothing usable.</summary>
    public Rectangle Bounds { get; set; }

    /// <summary>The whole window the control belongs to, in physical screen pixels.</summary>
    public Rectangle WindowBounds { get; set; }

    /// <summary>
    /// True when the click landed inside a window that is showing another computer. What is
    /// on screen there is a picture, so nothing inside it can be named.
    /// </summary>
    public bool IsRemoteSession { get; set; }

    public bool HasName => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>
/// Reads the accessibility tree to find out what was clicked. Every call is time boxed:
/// a slow or frozen application must never stall the recorder.
/// </summary>
public static class UiInspector
{
    private static readonly ConcurrentDictionary<int, string> AppNameCache = new();

    private static readonly ConcurrentDictionary<int, bool> RemoteCache = new();

    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = "Google Chrome",
        ["msedge"] = "Microsoft Edge",
        ["firefox"] = "Firefox",
        ["brave"] = "Brave",
        ["opera"] = "Opera",
        ["explorer"] = "File Explorer",
        ["notepad"] = "Notepad",
        ["winword"] = "Word",
        ["excel"] = "Excel",
        ["powerpnt"] = "PowerPoint",
        ["outlook"] = "Outlook",
        ["onenote"] = "OneNote",
        ["teams"] = "Microsoft Teams",
        ["ms-teams"] = "Microsoft Teams",
        ["slack"] = "Slack",
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["mstsc"] = "Remote Desktop",
        ["cmd"] = "Command Prompt",
        ["powershell"] = "Windows PowerShell",
        ["pwsh"] = "PowerShell",
        ["windowsterminal"] = "Windows Terminal",
        ["applicationframehost"] = "Windows app",
        ["shellexperiencehost"] = "Windows",
        ["searchhost"] = "Windows Search",
        ["startmenuexperiencehost"] = "Start menu",
        ["systemsettings"] = "Settings",
        ["taskmgr"] = "Task Manager",
        ["mmc"] = "Management Console",
        ["putty"] = "PuTTY",
        ["filezilla"] = "FileZilla",
        ["acrord32"] = "Adobe Reader",
        ["obs64"] = "OBS Studio",
        ["msrdc"] = "Remote Desktop",
        ["rdcman"] = "Remote Desktop Connection Manager",
        ["screenconnect.windowsclient"] = "ScreenConnect",
        ["connectwisecontrol.client"] = "ConnectWise Control",
        ["connectwisecontrol"] = "ConnectWise Control",
        ["srmanager3"] = "Splashtop",
        ["srclient"] = "Splashtop",
        ["splashtopbusiness"] = "Splashtop",
        ["teamviewer"] = "TeamViewer",
        ["anydesk"] = "AnyDesk",
        ["vncviewer"] = "VNC Viewer",
        ["tvnviewer"] = "TightVNC",
        ["dwrcc"] = "Dameware",
        ["logmein"] = "LogMeIn",
    };

    /// <summary>
    /// Applications that show another computer inside a window. The picture arriving from the
    /// far end has no accessibility tree, so a click there can never be given a control name,
    /// and the window title is the only thing the system will report.
    /// </summary>
    private static readonly HashSet<string> RemoteViewers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "msrdc", "rdcman", "screenconnect.windowsclient", "screenconnect.client",
        "connectwisecontrol.client", "connectwisecontrol", "srmanager3", "srclient",
        "splashtopbusiness", "strwinclt", "teamviewer", "tv_w32", "tv_x64", "anydesk",
        "vncviewer", "tvnviewer", "winvnc", "dwrcc", "logmein", "lmiguardiansvc", "rutserv",
        "supremo", "gotoassist", "beyondtrustrepresentativeconsole", "bomgar",
    };

    public static void ResetCache()
    {
        AppNameCache.Clear();
        RemoteCache.Clear();
    }

    /// <summary>Resolves the control under a screen point, falling back to plain window facts.</summary>
    public static ElementInfo Resolve(Point screenPoint, int timeoutMilliseconds = 700)
    {
        ElementInfo info = ResolveWindowOnly(screenPoint);
        AutomationElement? element = RunGuarded(
            () => AutomationElement.FromPoint(new System.Windows.Point(screenPoint.X, screenPoint.Y)),
            timeoutMilliseconds);

        if (element is not null)
        {
            FillFromAutomation(info, element, timeoutMilliseconds);
        }

        SettleRemote(info);
        return info;
    }

    /// <summary>
    /// Everything inside a remote window is a picture of another computer, so whatever the
    /// system reported is about the viewer and not about what the person actually clicked.
    /// Keeping it would put the viewer's own title, session number and running clock into
    /// every step. The name is dropped and the machine at the far end is named instead, which
    /// is true, short, and the same on every step.
    /// </summary>
    private static void SettleRemote(ElementInfo info)
    {
        if (!info.IsRemoteSession)
        {
            return;
        }

        info.Name = string.Empty;
        info.ControlType = string.Empty;
        info.ParentName = string.Empty;

        string machine = RemoteMachine(info.WindowTitle);
        string viewer = string.IsNullOrWhiteSpace(info.AppName) ? "a remote session" : info.AppName;

        info.AppName = machine.Length == 0
            ? viewer
            : $"{machine} through {viewer}";
    }

    /// <summary>
    /// The name of the far machine, as the viewer writes it in its title. Everything a viewer
    /// adds around it, the version, the session number and the clock, is dropped.
    /// </summary>
    private static string RemoteMachine(string windowTitle)
    {
        string steady = Steady(windowTitle);

        if (steady.Length == 0)
        {
            return string.Empty;
        }

        // Viewers write "machine - Product" or "machine: Product". The first part is the one
        // worth keeping.
        foreach (string separator in new[] { " - ", " – ", ": ", " | " })
        {
            int mark = steady.IndexOf(separator, StringComparison.Ordinal);
            if (mark > 0)
            {
                steady = steady[..mark];
                break;
            }
        }

        return Clean(steady).Trim(' ', '-', ':', '.', ',');
    }

    /// <summary>Resolves the control that currently has the keyboard focus.</summary>
    public static ElementInfo? ResolveFocused(int timeoutMilliseconds = 500)
    {
        AutomationElement? element = RunGuarded(() => AutomationElement.FocusedElement, timeoutMilliseconds);
        if (element is null)
        {
            return null;
        }

        var info = new ElementInfo();
        FillFromAutomation(info, element, timeoutMilliseconds);

        if (string.IsNullOrEmpty(info.AppName) && info.ProcessId > 0)
        {
            info.AppName = FriendlyAppName(info.ProcessId);
        }

        if (string.IsNullOrEmpty(info.WindowTitle))
        {
            info.WindowTitle = NativeMethods.GetWindowTitle(NativeMethods.GetForegroundWindow());
        }

        SettleRemote(info);
        return info;
    }

    private static ElementInfo ResolveWindowOnly(Point screenPoint)
    {
        var info = new ElementInfo();
        try
        {
            var point = new NativeMethods.POINT { X = screenPoint.X, Y = screenPoint.Y };
            IntPtr hwnd = NativeMethods.WindowFromPoint(point);
            IntPtr root = hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);

            info.WindowTitle = NativeMethods.GetWindowTitle(root);
            info.ClassName = NativeMethods.GetWindowClass(hwnd);

            if (root != IntPtr.Zero && NativeMethods.GetWindowRect(root, out NativeMethods.RECT area))
            {
                info.WindowBounds = new Rectangle(
                    area.Left,
                    area.Top,
                    Math.Max(0, area.Right - area.Left),
                    Math.Max(0, area.Bottom - area.Top));
            }

            if (root != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(root, out uint pid);
                info.ProcessId = (int)pid;
                info.AppName = FriendlyAppName((int)pid);
                info.IsRemoteSession = IsRemoteViewer((int)pid);
            }
        }
        catch
        {
            // Window queries are best effort.
        }

        return info;
    }

    private static void FillFromAutomation(ElementInfo info, AutomationElement element, int timeoutMilliseconds)
    {
        RunGuarded(
            () =>
            {
                AutomationElement.AutomationElementInformation current = element.Current;

                info.Name = Clean(current.Name);
                info.ControlType = Clean(current.LocalizedControlType);
                info.AutomationId = Clean(current.AutomationId);
                info.IsPassword = current.IsPassword;

                if (!string.IsNullOrEmpty(current.ClassName))
                {
                    info.ClassName = current.ClassName;
                }

                if (current.ProcessId > 0)
                {
                    info.ProcessId = current.ProcessId;
                    if (string.IsNullOrEmpty(info.AppName))
                    {
                        info.AppName = FriendlyAppName(current.ProcessId);
                    }

                    info.IsRemoteSession = info.IsRemoteSession || IsRemoteViewer(current.ProcessId);
                }

                System.Windows.Rect rect = current.BoundingRectangle;
                if (rect.Width > 0 && rect.Height > 0 && !double.IsInfinity(rect.Width) && !double.IsInfinity(rect.Height))
                {
                    info.Bounds = new Rectangle(
                        (int)Math.Round(rect.X),
                        (int)Math.Round(rect.Y),
                        (int)Math.Round(rect.Width),
                        (int)Math.Round(rect.Height));
                }

                ControlType type = current.ControlType;
                info.IsTextField = Equals(type, ControlType.Edit) || Equals(type, ControlType.Document) || Equals(type, ControlType.ComboBox);

                // A control that just repeats the window title tells the reader nothing.
                if (LooksLikeWindowTitle(info.Name, info.WindowTitle))
                {
                    info.Name = string.Empty;
                }

                if (!info.HasName || info.Name.Length > 120)
                {
                    ClimbForContext(info, element);
                }
                else
                {
                    info.ParentName = NameOfParent(element);
                }
            },
            timeoutMilliseconds);
    }

    /// <summary>Unnamed controls borrow a name from the nearest named ancestor.</summary>
    private static void ClimbForContext(ElementInfo info, AutomationElement element)
    {
        try
        {
            TreeWalker walker = TreeWalker.ControlViewWalker;
            AutomationElement? cursor = walker.GetParent(element);
            for (int depth = 0; depth < 4 && cursor is not null; depth++)
            {
                string name = Clean(cursor.Current.Name);
                if (LooksLikeWindowTitle(name, info.WindowTitle))
                {
                    // Climbing has reached the window itself, so there is no better name above.
                    return;
                }

                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 120)
                {
                    if (!info.HasName)
                    {
                        info.Name = name;
                        info.ControlType = Clean(cursor.Current.LocalizedControlType);
                    }
                    else
                    {
                        info.ParentName = name;
                    }

                    return;
                }

                cursor = walker.GetParent(cursor);
            }
        }
        catch
        {
            // The tree can change underneath the walker. Context is optional.
        }
    }

    private static string NameOfParent(AutomationElement element)
    {
        try
        {
            AutomationElement? parent = TreeWalker.ControlViewWalker.GetParent(element);
            return parent is null ? string.Empty : Clean(parent.Current.Name);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>True when this process is one that shows another computer inside a window.</summary>
    public static bool IsRemoteViewer(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        if (RemoteCache.TryGetValue(processId, out bool known))
        {
            return known;
        }

        bool remote;

        try
        {
            using Process process = Process.GetProcessById(processId);
            remote = RemoteViewers.Contains(process.ProcessName);
        }
        catch
        {
            // A process that has gone, or one this account may not look at.
            return false;
        }

        RemoteCache[processId] = remote;
        return remote;
    }

    public static string FriendlyAppName(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        if (AppNameCache.TryGetValue(processId, out string? known))
        {
            return known;
        }

        string resolved = Lookup(processId);

        // A blank answer means the lookup failed, so it is not worth remembering.
        if (!string.IsNullOrEmpty(resolved))
        {
            AppNameCache[processId] = resolved;
        }

        return resolved;
    }

    private static string Lookup(int id)
    {
        {
            try
            {
                using Process process = Process.GetProcessById(id);
                string raw = process.ProcessName;

                if (FriendlyNames.TryGetValue(raw, out string? friendly))
                {
                    return friendly;
                }

                try
                {
                    string? description = process.MainModule?.FileVersionInfo.FileDescription;
                    if (!string.IsNullOrWhiteSpace(description) && description.Length <= 40)
                    {
                        return description.Trim();
                    }
                }
                catch
                {
                    // Reading another process module is often blocked. Fall through to the name.
                }

                return raw.Length > 1 ? char.ToUpperInvariant(raw[0]) + raw[1..] : raw;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// True when a control name is really just the window title. A browser reports the page
    /// title on several of its containers, and repeating it back reads as noise.
    /// </summary>
    private static bool LooksLikeWindowTitle(string name, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(windowTitle))
        {
            return false;
        }

        string left = Steady(name);
        string right = Steady(windowTitle);

        if (left.Length == 0 || right.Length == 0)
        {
            return true;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Window titles carry the application on the end, so a control named after the first
        // part of the title counts too.
        return left.Length >= 12
            && (right.StartsWith(left, StringComparison.OrdinalIgnoreCase)
                || left.StartsWith(right, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A title with a clock in it, which is what every remote session window has, is a
    /// different string from one second to the next. The control name is read a moment after
    /// the window title, so the two never matched and the ticking title travelled into the
    /// step as though it were the name of a button. Dropping the parts that move on their own
    /// leaves the part that identifies the window.
    /// </summary>
    private static string Steady(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        int depth = 0;

        foreach (char letter in text)
        {
            if (letter is '[' or '(')
            {
                depth++;
                continue;
            }

            if (letter is ']' or ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth == 0)
            {
                result.Append(letter);
            }
        }

        return Clean(result.ToString()).Trim(' ', '-', ':', '.', ',');
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string text = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Runs an accessibility call with a hard deadline so a frozen app cannot block recording.</summary>
    private static T? RunGuarded<T>(Func<T> work, int timeoutMilliseconds)
        where T : class
    {
        try
        {
            // Long running, so a frozen application blocks a thread of its own rather than
            // starving the pool that the rest of the recorder depends on.
            Task<T?> task = Task.Factory.StartNew<T?>(
                () =>
                {
                    try
                    {
                        return work();
                    }
                    catch
                    {
                        return null;
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            return task.Wait(timeoutMilliseconds) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static void RunGuarded(Action work, int timeoutMilliseconds)
    {
        try
        {
            Task task = Task.Factory.StartNew(
                () =>
                {
                    try
                    {
                        work();
                    }
                    catch
                    {
                        // Property reads race with the application redrawing itself.
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            task.Wait(timeoutMilliseconds);
        }
        catch
        {
            // Deadline passed. The caller keeps whatever was filled in already.
        }
    }
}
