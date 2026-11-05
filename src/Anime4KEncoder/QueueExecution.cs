namespace Anime4KEncoder;

internal sealed class ResourcePauseException(string message) : InvalidOperationException(message);

internal static class QueueExecution
{
    internal static async Task RunItemAsync(Func<Task> run, Action<Exception> failed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { await run(); }
        catch (OperationCanceledException) { throw; }
        catch (ResourcePauseException) { throw; }
        catch (Exception ex) when (!token.IsCancellationRequested) { failed(ex); }
        token.ThrowIfCancellationRequested();
    }
}
