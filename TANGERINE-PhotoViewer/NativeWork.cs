
namespace TANGERINE_PhotoViewer;

internal static class NativeWork
{
    public static T Run<T>(Func<T> work, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var completed = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completed.TrySetResult(work()); }
            catch (Exception ex) { completed.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        while (!completed.Task.IsCompleted)
        {
            token.ThrowIfCancellationRequested();
            Thread.Sleep(20);
        }
        token.ThrowIfCancellationRequested();
        return completed.Task.GetAwaiter().GetResult();
    }
}

