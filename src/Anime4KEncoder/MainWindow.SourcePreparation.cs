using System.Diagnostics;
using System.IO;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private SourcePrefetch? _sourcePrefetch;

    private async Task<PreparedSource> PrepareSourceAsync(string input, string output,
        AnimeJanaiSegmentPlan plan, string pixelFormat, string logPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var partial = output + ".partial.mkv";
        var arguments = BuildLosslessSourceSegmentArguments(input, partial,
            plan.SourceStartFrame, plan.SourceFrameCount, pixelFormat);
        AppendLog(logPath, $"\n[{DateTime.Now:O}] Extração fonte bloco {plan.Index + 1}\n{FormatCommand(_ffmpeg, arguments)}\n");
        using var process = new Process { StartInfo = CreateProcessInfo(_ffmpeg, arguments, Path.GetDirectoryName(_ffmpeg)!) };
        StartWorker(process);
        using var registration = token.Register(() => KillProcess(process));
        // Background extraction must not overwrite the active encoder's progress, speed or stage.
        var stdout = ReadBoundedOutputAsync(process.StandardOutput, CancellationToken.None);
        var stderr = ReadBoundedOutputAsync(process.StandardError, CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(token);
            var error = await stderr;
            await stdout;
            token.ThrowIfCancellationRequested();
            AppendLog(logPath, $"\nExtração fonte bloco {plan.Index + 1}: ExitCode={process.ExitCode}\n{error}\n");
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Extração fonte: ExitCode={process.ExitCode}\n{Tail(error)}");
            var prepared = await ProbeMediaStructureAsync(partial, true, token);
            if (prepared.VideoPackets != plan.SourceFrameCount)
                throw new InvalidOperationException($"O trecho temporário {plan.Index + 1} contém {prepared.VideoPackets} frames; eram esperados {plan.SourceFrameCount}.");
            File.Move(partial, output, true);
            AppendLog(logPath, $"\n[{DateTime.Now:O}] Fonte bloco {plan.Index + 1} pronta: {watch.Elapsed.TotalSeconds:0.###}s.\n");
            return new(output, new FileInfo(output).Length, watch.Elapsed.TotalSeconds);
        }
        finally
        {
            KillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
        }
    }

    private void StartSourcePrefetch(AnimeJanaiSegmentPlan current, SegmentManifest manifest,
        int lastCachedIndex, MediaStructure source, string cacheDirectory, string logPath, CancellationToken token)
    {
        // Recovery retries reduce load rather than repeating the same speculative allocation.
        if (ReadResourcePercent() != 100 || (_backgroundRecovery?.Retries ?? 0) > 0 ||
            current.UsefulEndFrame >= source.VideoPackets) return;
        // Never mutate the real manifest here: the user can change modes before the boundary.
        var next = current.Index + 1 <= lastCachedIndex
            ? SegmentPlanning.FromCheckpoint(manifest.Segments[current.Index + 1])
            : SegmentPlanning.Create(current.Index + 1, current.UsefulEndFrame, source.VideoPackets,
                Math.Max(1, (long)Math.Round(source.AverageFrameRate * 300, MidpointRounding.AwayFromZero)), AnimeJanaiOverlapFrames);
        if (File.Exists(Path.Combine(cacheDirectory, SegmentFileName(next.Index)))) return;
        // Distinct from foreground source paths and from any recoverable encoded block.
        var path = Path.Combine(cacheDirectory, $"prefetch-{next.Index + 1:0000}-{next.SourceStartFrame}-{next.SourceEndFrame}.mkv");
        AppendLog(logPath, $"\n[{DateTime.Now:O}] Antecipando fonte bloco {next.Index + 1}; plano ainda não confirmado.\n");
        _sourcePrefetch = new SourcePrefetch(next, token,
            ct => PrepareSourceAsync(manifest.SourcePath, path, next, source.PixelFormat, logPath, ct));
    }

    private async Task<PreparedSource?> TakeSourcePrefetchAsync(AnimeJanaiSegmentPlan selected, string logPath, CancellationToken token)
    {
        var pending = _sourcePrefetch;
        if (pending is null) return null;
        try
        {
            var prepared = await pending.TakeAsync(selected, token);
            AppendLog(logPath, prepared is null
                ? $"\nAntecipação bloco {selected.Index + 1} não utilizada; respeitando modo e fronteira atuais.\n"
                : $"\n[{DateTime.Now:O}] Fonte antecipada bloco {selected.Index + 1} aproveitada e validada.\n");
            return prepared;
        }
        catch (Exception ex) when (!token.IsCancellationRequested && !QueueExecution.IsDiskFull(ex))
        {
            AppendLog(logPath, $"\nAntecipação indisponível; repetindo extração em série: {ex.Message}\n");
            return null;
        }
        finally
        {
            _sourcePrefetch = null;
            await pending.DisposeAsync();
        }
    }

    private async Task StopSourcePrefetchAsync()
    {
        var pending = _sourcePrefetch;
        _sourcePrefetch = null;
        if (pending is not null) await pending.DisposeAsync();
    }
}
