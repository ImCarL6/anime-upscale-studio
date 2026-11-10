namespace Anime4KEncoder;

internal sealed record PreparedSource(string Path, long Bytes, double ExtractionSeconds);

// Speculation is never a checkpoint. Only the boundary planner commits a layout.
internal sealed class SourcePrefetch : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    internal AnimeJanaiSegmentPlan Plan { get; }
    internal Task<PreparedSource> Preparation { get; }

    internal SourcePrefetch(AnimeJanaiSegmentPlan plan, CancellationToken token,
        Func<CancellationToken, Task<PreparedSource>> prepare)
    {
        Plan = plan;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Preparation = prepare(_cancellation.Token);
    }

    internal void Cancel() => _cancellation.Cancel();

    internal async Task<PreparedSource?> TakeAsync(AnimeJanaiSegmentPlan selected, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (selected != Plan || _cancellation.IsCancellationRequested) return null;
        try { return await Preparation; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        // Drain before releasing the attempt's Job Object or starting a replacement.
        _cancellation.Cancel();
        try { await Preparation; }
        catch { /* The consumer handles errors; abandoned speculation cannot fail the current block. */ }
        finally { _cancellation.Dispose(); }
    }
}
