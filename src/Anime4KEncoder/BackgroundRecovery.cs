namespace Anime4KEncoder;

internal sealed class BackgroundRecovery
{
    internal const int MaxRetries = 2;
    internal int Retries { get; private set; }
    internal double ReadRate(int mode) => (mode == 70 ? 0.15 : 0.25) / (1 + Retries);
    internal bool TryRetry(bool recoverable, bool userCancelled)
    {
        if (!recoverable || userCancelled || Retries >= MaxRetries) return false;
        Retries++;
        return true;
    }
    internal static bool AllocationFailure(string message) => new[]
    {
        "out of memory", "cannot allocate memory", "failed to allocate", "CUDA_ERROR_OUT_OF_MEMORY",
        "sem memória", "bad_alloc", "not enough memory"
    }.Any(value => message.Contains(value, StringComparison.OrdinalIgnoreCase));
}
