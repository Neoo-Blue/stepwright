namespace Stepwright.Web;

/// <summary>
/// Runs work on the thread that owns the windows.
///
/// The embedded browser is a window like any other, so every call into it has to happen on the
/// thread that made it. The assistant, meanwhile, is written the way network code should be
/// written, with no assumptions about which thread it is on. This is the one place those two
/// facts are reconciled, rather than scattering thread checks through both.
/// </summary>
public static class UiThread
{
    private static Control? _anchor;

    /// <summary>Called once, by the first window, so everything after it knows where to go.</summary>
    public static void Attach(Control anchor) => _anchor = anchor;

    public static bool Attached => _anchor is not null;

    public static async Task<T> RunAsync<T>(Func<Task<T>> work)
    {
        Control? anchor = _anchor;

        if (anchor is null || anchor.IsDisposed || !anchor.InvokeRequired)
        {
            return await work().ConfigureAwait(true);
        }

        var finished = new TaskCompletionSource<T>();

        anchor.BeginInvoke(async () =>
        {
            try
            {
                finished.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception failure)
            {
                finished.TrySetException(failure);
            }
        });

        return await finished.Task.ConfigureAwait(false);
    }

    public static Task RunAsync(Func<Task> work) =>
        RunAsync(async () =>
        {
            await work().ConfigureAwait(true);
            return true;
        });
}
