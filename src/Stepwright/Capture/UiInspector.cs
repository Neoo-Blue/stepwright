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

    public bool HasName => !string.IsNullOrWhiteSpace(Name);
}

/// <summary>
/// Reads the accessibility tree to find out what was clicked. Every call is time boxed:
/// a slow or frozen application must never stall the recorder.
/// </summary>
public static class UiInspector
{
    private static readonly ConcurrentDictionary<int, string> AppNameCache = new();

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
    };

    public static void ResetCache() => AppNameCache.Clear();

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

        return info;
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

            if (root != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(root, out uint pid);
                info.ProcessId = (int)pid;
                info.AppName = FriendlyAppName((int)pid);
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
