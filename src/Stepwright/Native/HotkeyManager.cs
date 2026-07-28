namespace Stepwright.Native;

/// <summary>Registers system wide shortcut keys against a hidden window.</summary>
public sealed class HotkeyManager : NativeWindow, IDisposable
{
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 0x5100;
    private bool _disposed;

    public HotkeyManager()
    {
        CreateHandle(new CreateParams());
    }

    /// <summary>Returns false when another program already owns the combination.</summary>
    public bool Register(uint modifiers, uint virtualKey, Action action)
    {
        int id = _nextId++;
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, virtualKey))
        {
            return false;
        }

        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (int id in _actions.Keys.ToList())
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }

        _actions.Clear();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY
            && _actions.TryGetValue((int)m.WParam, out Action? action))
        {
            try
            {
                action();
            }
            catch
            {
                // A failing shortcut must never take the message loop down.
            }
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterAll();
        DestroyHandle();
    }
}
