using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private ResourceProtection? _resourceProtection;
    private bool _resourceControlsReady;
    private string? _resourceStopReason;
    private BackgroundRecovery? _backgroundRecovery;
    private bool _allocationFailed;
    private ResourceSnapshot? _lastResourceSnapshot;

    private async Task WaitForResourceReserveAsync(CancellationToken token, bool cooldown)
    {
        using var nvml = NvidiaMemoryReader.TryCreate();
        var started = Stopwatch.StartNew();
        var healthy = 0;
        var cooldownSeconds = cooldown ? 30 : 0;
        while (started.Elapsed < TimeSpan.FromMinutes(5))
        {
            token.ThrowIfCancellationRequested();
            var snapshot = ResourceProtection.Sample(nvml);
            healthy = ResourcePolicy.Ready(snapshot) ? healthy + 1 : 0;
            if (healthy >= 5 && started.Elapsed.TotalSeconds >= cooldownSeconds) return;
            ResourceStatusText.Text = $"Aguardando reserva ({started.Elapsed:mm\\:ss}/05:00) • RAM livre {FormatBytes((long)snapshot.AvailableRam)} • compromisso livre {FormatBytes((long)snapshot.AvailableCommit)} • pode cancelar";
            await Task.Delay(1000, token);
        }
        throw new InvalidOperationException("Não houve reserva suficiente em 5 minutos. A fila foi pausada, com os blocos preservados.");
    }

    private async Task EncodeInBackgroundAsync(EncodeItem item, JobSettings settings, CancellationToken token)
    {
        _backgroundRecovery = new BackgroundRecovery();
        try
        {
            while (true)
            {
                item.IsRunning = true;
                item.Stage = _backgroundRecovery.Retries > 0 ? $"Aguardando retomada {_backgroundRecovery.Retries}/2" : "Aguardando reserva de recursos";
                await WaitForResourceReserveAsync(token, _backgroundRecovery.Retries > 0);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                _allocationFailed = false;
                BeginResourceProtection(ReadResourcePercent(), attempt);
                try { await EncodeAsync(item, settings, attempt.Token); }
                finally { EndResourceProtection(); }
                token.ThrowIfCancellationRequested();
                var recoverable = _resourceStopReason is not null || _allocationFailed;
                if (!recoverable) return;
                if (settings.Engine != "animejanai" || !string.IsNullOrWhiteSpace(settings.AdvancedFfmpegCommand))
                    throw new InvalidOperationException("A retomada automática exige AnimeJaNai por blocos. Arquivos preservados.");
                if (!_backgroundRecovery.TryRetry(recoverable, token.IsCancellationRequested))
                {
                    item.Status = "Falhou";
                    item.Error = "Reserva insuficiente após duas retomadas automáticas. Blocos preservados; fila pausada para evitar repetição.";
                    throw new InvalidOperationException(item.Error);
                }
                AppendLog(item.LogPath, $"\nRetomada automática {_backgroundRecovery.Retries}/2; ritmo={_backgroundRecovery.ReadRate(settings.ResourcePercent):0.###}x; {_resourceStopReason ?? "falha de alocação"}\n");
                item.Stage = $"Aguardando retomada {_backgroundRecovery.Retries}/2";
                item.Status = "Aguardando";
            }
        }
        catch (OperationCanceledException) { item.Status = "Cancelado"; throw; }
        catch (Exception ex) { item.Status = "Falhou"; item.Error = ex.Message; throw; }
        finally { item.IsRunning = false; _backgroundRecovery = null; }
    }

    private LivePacing? _livePacing;
    private bool _resourceModeChangedDuringRun;
    private double CurrentPace() => ReadResourcePercent() == 100 ? 1000000 :
        (_backgroundRecovery ?? new BackgroundRecovery()).ReadRate(ReadResourcePercent());

    private LivePacing? CreateLivePacing(List<string> arguments, JobSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AdvancedFfmpegCommand)) return null;
        var index = arguments.IndexOf("-vf");
        if (index < 0) return null;
        var pacing = new LivePacing(CurrentPace());
        arguments[index + 1] = pacing.Filter + "," + arguments[index + 1];
        _livePacing = pacing;
        return pacing;
    }
    private int ReadResourcePercent() => ResourceModeCombo.SelectedIndex switch { 0 => 70, 1 => 80, _ => 100 };
    private int ReadConcurrency() => 1;

    private void InitializeResourceControls()
    {
        try
        {
            var path = Path.Combine(_dataRoot, "data", "resource-settings.json");
            if (File.Exists(path))
            {
                var percent = JsonSerializer.Deserialize<int>(File.ReadAllText(path));
                ResourceModeCombo.SelectedIndex = percent switch { 70 => 0, 100 => 2, _ => 1 };
            }
        }
        catch { /* A missing/corrupt setting falls back to the protected default. */ }
        _resourceControlsReady = true;
        ResourceMode_Changed(this, null!);
    }

    private void ResourceMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_resourceControlsReady) return;
        MaximumPerformanceButton.Content = ReadResourcePercent() == 100 ? "⚡ Desempenho máximo ativo" : "⚡ Ativar desempenho máximo";
        try
        {
            _resourceProtection?.ApplyMode(ReadResourcePercent());
            _livePacing?.SetRate(CurrentPace());
        }
        catch (Exception ex)
        {
            ResourceStatusText.Text = "Não foi possível aplicar o modo: " + ex.Message;
            return;
        }
        if (_queueCancellation is not null)
        {
            _resourceModeChangedDuringRun = true;
            foreach (var item in Items.Where(item => item.IsRunning && !string.IsNullOrEmpty(item.LogPath)))
                AppendLog(item.LogPath, $"\nTroca ao vivo: CPU={ResourceProtection.CpuPercent(ReadResourcePercent())}%, ritmo={CurrentPace():0.###}x.\n");
        }
        if (ReadResourcePercent() < 100) ConcurrencyCombo.SelectedIndex = 0;
        ConcurrencyCombo.IsEnabled = false;
        ResourceStatusText.Text = ReadResourcePercent() < 100
            ? "Controle ao vivo • 1 vídeo por vez • ritmo reduzido • espera por reserva • até 2 retomadas automáticas"
            : "Desempenho liberado • proteção de memória e retomada continuam ativas";
        try
        {
            var path = Path.Combine(_dataRoot, "data", "resource-settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(ReadResourcePercent()));
        }
        catch { ResourceStatusText.Text += " • preferência não pôde ser salva"; }
        _lastPilotResult = null;
        _activeToolchainFingerprint = "";
        UpdateEstimates();
        SchedulePilotCacheRefresh();
    }

    private void BeginResourceProtection(int percent, CancellationTokenSource? attempt = null)
    {
        _resourceStopReason = null;

        _resourceProtection = new ResourceProtection(percent, attempt ?? _queueCancellation!, (snapshot, note) =>
        {
            _lastResourceSnapshot = snapshot;
            Dispatcher.BeginInvoke(() =>
            {
                var gpuText = snapshot.Gpus.Count > 0
                    ? string.Join(" / ", snapshot.Gpus.Select(gpu => $"VRAM {100d * gpu.Used / gpu.Total:0}%"))
                    : "VRAM indisponível";
                ResourceStatusText.Text = $"RAM livre {FormatBytes((long)snapshot.AvailableRam)} • compromisso livre {FormatBytes((long)snapshot.AvailableCommit)} • {gpuText} • CPU até {ResourceProtection.CpuPercent(ReadResourcePercent())}% • {(_livePacing is { IsDisposed: false } live ? (live.AppliedRate == CurrentPace() ? "ritmo aplicado" : live.Error ?? "aplicando ritmo…") : "preparando etapa")} • {note}";
            });
        });
    }

    private void EndResourceProtection()
    {
        var protection = _resourceProtection;
        protection?.Dispose();
        _resourceStopReason = protection?.StopReason;
        _resourceProtection = null;
        if (_resourceStopReason is not null)
        {
            ResourceStatusText.Text = _resourceStopReason + (_backgroundRecovery is not null ? " Preparando retomada automática." : " Blocos preservados.");
            foreach (var item in Items.Where(item => item.Status == "Cancelado")) item.Stage = "Interrompido para proteger o PC";
        }
    }

    private void StartWorker(Process process)
    {
        if (_resourceProtection is { } protection) protection.Start(process);
        else if (!process.Start()) throw new InvalidOperationException("Não foi possível iniciar o processo de trabalho.");
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        // Keep the diagnostic tail, not the entire native log in RAM for a long encode.
        var output = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            output.Append(buffer, 0, count);
            if (output.Length > 60000) output.Remove(0, output.Length - 60000);
        }
        return output.ToString();
    }
}
