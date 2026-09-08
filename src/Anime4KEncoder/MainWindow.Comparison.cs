using System.IO;
using System.Windows;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private PilotComparisonWindow? _comparisonWindow;

    private async Task<PilotComparisonResult> CreatePilotComparisonAsync(string originalPilot, string processedPilot,
        IReadOnlyList<PilotSamplePlan> samples, double frameRate, int width, int height, string profile,
        string logPath, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(processedPilot)!, "comparison");
        Directory.CreateDirectory(directory);
        var frames = new List<PilotComparisonFrame>();
        foreach (var plan in ComparisonPlanner.Build(samples.Select(s => (s.StartFrame, s.FrameCount))))
        {
            var originalPath = Path.Combine(directory, $"{plan.SampleIndex + 1:00}-original.png");
            var processedPath = Path.Combine(directory, $"{plan.SampleIndex + 1:00}-processed.png");
            await ExtractComparisonFrameAsync(originalPilot, plan.PilotFrame, width, height, originalPath, logPath, cancellationToken);
            await ExtractComparisonFrameAsync(processedPilot, plan.PilotFrame, width, height, processedPath, logPath, cancellationToken);
            frames.Add(new(plan.SampleIndex, plan.OriginalFrame, plan.PilotFrame, originalPath, processedPath));
        }
        return new(width, height, frameRate, profile, frames);
    }

    private async Task ExtractComparisonFrameAsync(string input, long frame, int width, int height, string output,
        string logPath, CancellationToken cancellationToken)
    {
        // Select the exact frame of the short consolidated pilot; resize the source without AI.
        var arguments = new[] { "-hide_banner", "-nostdin", "-y", "-v", "error", "-i", input,
            "-map", "0:v:0", "-vf", $"select=eq(n\\,{frame}),scale={width}:{height}:flags=lanczos,format=rgb24",
            "-frames:v", "1", "-fps_mode", "passthrough", "-update", "1", output };
        using var process = System.Diagnostics.Process.Start(CreateProcessInfo(_ffmpeg, arguments, Path.GetDirectoryName(_ffmpeg)!))
            ?? throw new InvalidOperationException("Não foi possível extrair o quadro de comparação.");
        using var registration = cancellationToken.Register(() => KillProcess(process));
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var detail = await stdout + await stderr;
        AppendLog(logPath, $"\nComparação: frame={frame}; arquivo={output}; ExitCode={process.ExitCode}\n{detail}\n");
        if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidOperationException("Falha ao extrair comparação: " + detail);
    }

    private static bool HasComparison(PilotRunResult? result) => result?.Comparison is { Frames.Count: > 0 } comparison &&
        comparison.Frames.All(frame => File.Exists(frame.OriginalPath) && File.Exists(frame.ProcessedPath));

    private void ComparePilot_Click(object sender, RoutedEventArgs e) => OpenPilotComparison();

    private void OpenPilotComparison()
    {
        if (!HasComparison(_lastPilotResult))
        {
            MessageBox.Show(this, "Este piloto não tem imagens de comparação disponíveis. Execute um novo piloto para gerá-las.", "Comparação do piloto");
            return;
        }
        try
        {
            _comparisonWindow?.Close();
            _comparisonWindow = new PilotComparisonWindow(_lastPilotResult!.Comparison!) { Owner = this };
            _comparisonWindow.Show();
        }
        catch (Exception ex) { MessageBox.Show(this, "Não foi possível abrir a comparação.\n" + ex.Message, "Comparação do piloto"); }
    }
}
