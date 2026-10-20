using System.Diagnostics;
using System.IO;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private readonly HashSet<string> _validatedInputs = new(StringComparer.OrdinalIgnoreCase);
    private static string InputIdentity(string path)
    {
        var file = new FileInfo(path);
        return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
    }

    private async Task ValidateInputIntegrityAsync(EncodeItem item, CancellationToken token)
    {
        var identity = InputIdentity(item.InputPath);
        var media = await ProbeMediaAsync(item.InputPath, token);
        item.ApplyMediaInfo(media);
        if (_validatedInputs.Contains(identity)) return;
        item.Stage = "Verificando integridade da entrada";
        item.Status = "Validando entrada";
        var timeline = new InputTimeline();
        var args = new[] { "-v", "error", "-show_packets", "-show_entries", "packet=codec_type,pts_time,duration_time", "-of", "compact=p=0:nk=0", item.InputPath };
        using (var process = new Process { StartInfo = CreateProcessInfo(_ffprobe, args, Path.GetDirectoryName(_ffprobe)!) })
        {
            StartWorker(process);
            using var cancellation = token.Register(() => KillProcess(process));
            var errors = ReadBoundedOutputAsync(process.StandardError, token);
            while (await process.StandardOutput.ReadLineAsync(token) is { } line) timeline.Add(line);
            await process.WaitForExitAsync(token);
            var error = await errors;
            AppendLog(item.LogPath, $"\nInventário integral da entrada\n{error}\nExitCode={process.ExitCode}; fim A/V={timeline.AudioVideoEnd:0.###}; fim legendas={timeline.SubtitleEnd:0.###}; frames={timeline.VideoPackets}\n");
            InputTimeline.ValidateReader(process.ExitCode, error);
            timeline.Validate(media.Duration.TotalSeconds);
        }
        item.Stage = "Validando vídeo e todos os áudios da entrada";
        var decodeArgs = new[] { "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-threads", "4", "-filter_threads", "1", "-i", item.InputPath,
            "-map", "0:v:0", "-map", "0:a?", "-sn", "-dn", "-threads", "1", "-fps_mode", "passthrough", "-progress", "pipe:1", "-nostats", "-f", "null", "NUL" };
        using (var process = new Process { StartInfo = CreateProcessInfo(_ffmpeg, decodeArgs, Path.GetDirectoryName(_ffmpeg)!) })
        {
            StartWorker(process);
            using var cancellation = token.Register(() => KillProcess(process));
            var output = ReadInputDecodeProgressAsync(process, item, token);
            var errors = ReadBoundedOutputAsync(process.StandardError, token);
            await process.WaitForExitAsync(token);
            var decodedFrames = await output;
            var error = await errors;
            AppendLog(item.LogPath, $"\nDecodificação integral da entrada (vídeo e todos os áudios)\n{error}\nExitCode={process.ExitCode}\n");
            InputTimeline.ValidateReader(process.ExitCode, error);
            if (decodedFrames != timeline.VideoPackets)
                throw new InvalidOperationException($"A entrada produziu {decodedFrames} frames decodificados, mas contém {timeline.VideoPackets} pacotes de vídeo. Verifique o arquivo antes do upscale.");
        }
        if (identity != InputIdentity(item.InputPath))
            throw new InvalidOperationException("O arquivo de entrada foi alterado durante a validação. Aguarde a cópia/download terminar.");
        _validatedInputs.Add(identity);
    }

    private async Task<long> ReadInputDecodeProgressAsync(Process process, EncodeItem item, CancellationToken token)
    {
        long frames = 0;
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            var pair = line.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "frame" && long.TryParse(pair[1].Trim(), out var count)) frames = count;
            if (pair[0] == "out_time_us" && long.TryParse(pair[1], out var microseconds) && microseconds >= 0)
            {
                var elapsed = TimeSpan.FromTicks(microseconds * 10);
                await Dispatcher.InvokeAsync(() => item.Stage = $"Validando entrada: {FormatDuration(elapsed)} / {FormatDuration(item.Duration)}");
            }
        }
        return frames;
    }
}
