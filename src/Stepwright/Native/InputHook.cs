using System.Runtime.InteropServices;
using System.Text;

namespace Stepwright.Native;

public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
    Extra
}

public sealed class MouseInputEventArgs : EventArgs
{
    public MouseButtonKind Button { get; init; }
    public Point Location { get; init; }
    public bool IsDown { get; init; }
    public int WheelDelta { get; init; }
    public bool Horizontal { get; init; }
    public DateTime Moment { get; init; } = DateTime.Now;
}

public sealed class KeyInputEventArgs : EventArgs
{
    public uint VirtualKey { get; init; }
    public uint ScanCode { get; init; }
    public bool Shift { get; init; }
    public bool Control { get; init; }
    public bool Alt { get; init; }
    public bool Windows { get; init; }

    /// <summary>The character the key produced, or null when the key is not a text key.</summary>
    public string? Text { get; init; }

    public DateTime Moment { get; init; } = DateTime.Now;

    /// <summary>Set by a subscriber to swallow the key so it never reaches the focused app.</summary>
    public bool Swallow { get; set; }
}

/// <summary>
/// System wide low level mouse and keyboard hooks. Install and dispose on the UI thread:
/// the callbacks arrive on the thread that owns the message loop, so subscribers must stay fast.
/// </summary>
public sealed class InputHook : IDisposable
{
    private readonly NativeMethods.HookProc _mouseProc;
    private readonly NativeMethods.HookProc _keyProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyHook = IntPtr.Zero;
    private bool _disposed;

    /// <summary>
    /// Shift lock as seen by the keys passing through the hook. The system answer cannot be
    /// used, because it reflects the calling thread's own message queue and this thread never
    /// sees the keystrokes going to another application.
    /// </summary>
    private bool _capsLock;

    public event EventHandler<MouseInputEventArgs>? MouseAction;
    public event EventHandler<KeyInputEventArgs>? KeyAction;

    public bool Suspended { get; set; }

    public InputHook()
    {
        // The delegates are stored in fields so the garbage collector cannot move or collect them
        // while Windows still holds the function pointers.
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
    }

    public bool IsInstalled => _mouseHook != IntPtr.Zero || _keyHook != IntPtr.Zero;

    public void Install()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsInstalled)
        {
            return;
        }

        IntPtr module = NativeMethods.GetModuleHandleW(null);

        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
        int mouseError = Marshal.GetLastWin32Error();

        _keyHook = NativeMethods.SetWindowsHookExW(NativeMethods.WH_KEYBOARD_LL, _keyProc, module, 0);
        int keyError = Marshal.GetLastWin32Error();

        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
        {
            int error = _mouseHook == IntPtr.Zero ? mouseError : keyError;
            Uninstall();
            throw new InvalidOperationException($"Windows refused the input hook (error {error}).");
        }

        // Seed the shift lock from the system now, then follow it key by key. Asking the
        // system later would be wrong, because this thread never receives the keystrokes
        // that change it.
        _capsLock = (NativeMethods.GetKeyState(NativeMethods.VK_CAPITAL) & 1) != 0;
    }

    public void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION || Suspended)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        try
        {
            int message = (int)wParam;
            if (message != NativeMethods.WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

                // Injected events come from automation tools, not from the person recording.
                bool injected = (data.flags & 0x01) != 0;
                if (!injected)
                {
                    RaiseMouse(message, data);
                }
            }
        }
        catch
        {
            // A throwing hook would be silently removed by Windows, so swallow everything.
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void RaiseMouse(int message, NativeMethods.MSLLHOOKSTRUCT data)
    {
        var point = new Point(data.pt.X, data.pt.Y);
        MouseInputEventArgs? args = message switch
        {
            NativeMethods.WM_LBUTTONDOWN => new MouseInputEventArgs { Button = MouseButtonKind.Left, Location = point, IsDown = true },
            NativeMethods.WM_LBUTTONUP => new MouseInputEventArgs { Button = MouseButtonKind.Left, Location = point, IsDown = false },
            NativeMethods.WM_RBUTTONDOWN => new MouseInputEventArgs { Button = MouseButtonKind.Right, Location = point, IsDown = true },
            NativeMethods.WM_RBUTTONUP => new MouseInputEventArgs { Button = MouseButtonKind.Right, Location = point, IsDown = false },
            NativeMethods.WM_MBUTTONDOWN => new MouseInputEventArgs { Button = MouseButtonKind.Middle, Location = point, IsDown = true },
            NativeMethods.WM_MBUTTONUP => new MouseInputEventArgs { Button = MouseButtonKind.Middle, Location = point, IsDown = false },
            NativeMethods.WM_MOUSEWHEEL => new MouseInputEventArgs
            {
                Button = MouseButtonKind.Middle,
                Location = point,
                IsDown = false,
                WheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF)),
            },
            NativeMethods.WM_MOUSEHWHEEL => new MouseInputEventArgs
            {
                Button = MouseButtonKind.Middle,
                Location = point,
                IsDown = false,
                Horizontal = true,
                WheelDelta = unchecked((short)((data.mouseData >> 16) & 0xFFFF)),
            },
            _ => null,
        };

        if (args is not null)
        {
            MouseAction?.Invoke(this, args);
        }
    }

    private IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode != NativeMethods.HC_ACTION || Suspended)
        {
            return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
        }

        try
        {
            int message = (int)wParam;
            if (message == NativeMethods.WM_KEYDOWN || message == NativeMethods.WM_SYSKEYDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                bool injected = (data.flags & 0x10) != 0;
                if (!injected)
                {
                    if (data.vkCode == NativeMethods.VK_CAPITAL)
                    {
                        _capsLock = !_capsLock;
                    }

                    var args = BuildKeyArgs(data);
                    KeyAction?.Invoke(this, args);
                    if (args.Swallow)
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }
        catch
        {
            // Never let an exception escape a hook callback.
        }

        return NativeMethods.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private KeyInputEventArgs BuildKeyArgs(NativeMethods.KBDLLHOOKSTRUCT data)
    {
        bool shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
        bool control = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL);
        bool alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU);
        bool windows = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);

        // Control or Alt on their own turn a key into a command rather than text.
        // Both together is the AltGr combination on international layouts, which still types.
        bool commandLike = (control ^ alt) || windows;
        string? text = commandLike ? null : TranslateToText(data.vkCode, data.scanCode, shift, _capsLock);

        return new KeyInputEventArgs
        {
            VirtualKey = data.vkCode,
            ScanCode = data.scanCode,
            Shift = shift,
            Control = control,
            Alt = alt,
            Windows = windows,
            Text = text,
        };
    }

    private static string? TranslateToText(uint vk, uint scan, bool shift, bool capsLock)
    {
        switch (vk)
        {
            case NativeMethods.VK_SHIFT:
            case NativeMethods.VK_CONTROL:
            case NativeMethods.VK_MENU:
            case NativeMethods.VK_LWIN:
            case NativeMethods.VK_RWIN:
            case NativeMethods.VK_CAPITAL:
            case NativeMethods.VK_LSHIFT:
            case NativeMethods.VK_RSHIFT:
            case NativeMethods.VK_LCONTROL:
            case NativeMethods.VK_RCONTROL:
            case NativeMethods.VK_LMENU:
            case NativeMethods.VK_RMENU:
                return null;
        }

        var state = new byte[256];
        if (shift)
        {
            state[NativeMethods.VK_SHIFT] = 0x80;
            state[NativeMethods.VK_LSHIFT] = 0x80;
        }

        if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL) && NativeMethods.IsKeyDown(NativeMethods.VK_MENU))
        {
            state[NativeMethods.VK_CONTROL] = 0x80;
            state[NativeMethods.VK_MENU] = 0x80;
        }

        if (capsLock)
        {
            state[NativeMethods.VK_CAPITAL] = 1;
        }

        IntPtr layout = IntPtr.Zero;
        try
        {
            uint thread = NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out _);
            layout = NativeMethods.GetKeyboardLayout(thread);
        }
        catch
        {
            // Fall back to the default layout below.
        }

        var buffer = new StringBuilder(8);

        // Flag 0x4 asks Windows not to disturb the live dead key state of the keyboard.
        int result = NativeMethods.ToUnicodeEx(vk, scan, state, buffer, buffer.Capacity, 4, layout);
        if (result <= 0)
        {
            return null;
        }

        string text = buffer.ToString(0, Math.Min(result, buffer.Length));
        if (text.Length == 0)
        {
            return null;
        }

        // Control characters are handled as named keys instead of text.
        return char.IsControl(text[0]) ? null : text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Uninstall();
    }
}
