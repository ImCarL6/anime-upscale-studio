using System.IO;

namespace Anime4KEncoder;

internal class ResourcePauseException(string message) : InvalidOperationException(message);

/// <summary>
/// Disco cheio é uma condição do computador, não do episódio: pausa a fila em vez de marcar
/// cada episódio seguinte como "Falhou".
/// </summary>
internal sealed class DiskSpacePauseException(string message) : ResourcePauseException(message);

internal static class QueueExecution
{
    internal const string DiskFullMessage =
        "O disco de saída ficou sem espaço. A fila foi pausada e os blocos validados foram preservados; libere espaço e inicie a fila novamente para retomar.";

    private const int ErrorHandleDiskFull = 0x27;
    private const int ErrorDiskFull = 0x70;

    private static readonly string[] DiskFullMarkers =
    [
        "No space left on device", "error code: -28", "ExitCode=-28",
        "There is not enough space on the disk", "Não há espaço suficiente no disco", "espaço insuficiente no disco"
    ];

    internal static bool IsDiskFull(string? text) =>
        !string.IsNullOrEmpty(text) && DiskFullMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    internal static bool IsDiskFull(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is IOException io && (io.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull) return true;
            if (IsDiskFull(current.Message)) return true;
        }
        return false;
    }

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
