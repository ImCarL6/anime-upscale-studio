using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private readonly Stopwatch _queueWatch = new();
    private double _savedQueueSeconds;
    private string _inputDirectory = "";
    private bool _sessionReady;
    private DispatcherTimer? _sessionTimer;
    private int _addingFiles;
    private TimeSpan QueueElapsed => TimeSpan.FromSeconds(Math.Max(0, _savedQueueSeconds)) + _queueWatch.Elapsed;
    private string SessionPath => Path.Combine(_dataRoot, "data", "queue-session.json");

    private void InitializeSession()
    {
        Items.CollectionChanged += QueueCollectionChanged;
        var saved = SessionStore.Load(SessionPath);
        if (saved is not null)
        {
            var restoreSettings = saved.Items.Any(entry => entry.Status is not ("Concluído" or "Saída já existe"));
            if (restoreSettings)
            {
                var engine = _engines.FirstOrDefault(engine => engine.Code == saved.Engine);
                if (engine is not null) EngineCombo.SelectedItem = engine;
                var profile = ProfileCombo.Items.Cast<IProcessingProfile>().FirstOrDefault(profile => profile.Code == saved.Profile);
                if (profile is not null) ProfileCombo.SelectedItem = profile;
                if (saved.Cq is >= 0 and <= 63) CqText.Text = saved.Cq.Value.ToString();
                if (saved.AqStrength10.HasValue) BetaAqStrengthCheck.IsChecked = saved.AqStrength10.Value;
                if (saved.BAdapt.HasValue) BetaBAdaptCheck.IsChecked = saved.BAdapt.Value;
            }
            _inputDirectory = saved.InputDirectory;
            _savedQueueSeconds = double.IsFinite(saved.ElapsedSeconds) ? Math.Max(0, saved.ElapsedSeconds) : 0;
            OutputDirectoryText.Text = saved.OutputDirectory;
            foreach (var entry in saved.Items.Where(entry => !string.IsNullOrWhiteSpace(entry.InputPath)).DistinctBy(entry => entry.InputPath, StringComparer.OrdinalIgnoreCase))
            {
                var item = new EncodeItem(entry.InputPath);
                if (entry.Media is { } media && (media.Size > 0 || File.Exists(entry.InputPath))) item.ApplyMediaInfo(media);
                item.OutputPath = entry.OutputPath;
                item.Status = entry.Status is "Concluído" or "Saída já existe" or "Falhou" or "Cancelado" ? entry.Status : "Aguardando";
                item.Stage = item.Status == "Concluído" ? "Finalizado" : "Restaurado da sessão anterior";
                item.Progress = Math.Clamp(entry.Progress, 0, 100);
                item.Processed = item.Status == "Concluído" ? item.Duration : TimeSpan.Zero;
                item.RestoreElapsed(TimeSpan.FromSeconds(Math.Max(0, entry.ElapsedSeconds)));
                item.ActualOutputBytes = entry.OutputBytes;
                item.Error = entry.Error;
                item.LogPath = entry.LogPath;
                Items.Add(item);
            }
            if (restoreSettings && saved.AdvancedMode)
            {
                AdvancedModeCheck.IsChecked = true;
                AdvancedPanel.Visibility = Visibility.Visible;
                AdvancedCommandText.Text = saved.AdvancedCommand;
            }
        }
        _sessionReady = true;
        OutputDirectoryText.TextChanged += (_, _) => SaveSession();
        _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sessionTimer.Tick += (_, _) => SaveSession();
        _sessionTimer.Start();
        Closed += (_, _) => { SaveSession(); _sessionTimer.Stop(); };
        RefreshOverall();
        UpdateEstimates();
        UpdateOutputDiskSpace();
    }

    private void QueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null) foreach (EncodeItem item in e.OldItems) item.PropertyChanged -= QueueItemChanged;
        if (e.NewItems is not null) foreach (EncodeItem item in e.NewItems) item.PropertyChanged += QueueItemChanged;
        if (_sessionReady && Items.Count == 0 && !_queueWatch.IsRunning)
        {
            _savedQueueSeconds = 0;
            _queueWatch.Reset();
        }
        SaveSession();
    }

    private void QueueItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EncodeItem.Status) or nameof(EncodeItem.IsRunning) or nameof(EncodeItem.OutputPath)) SaveSession();
    }

    private void SaveSession()
    {
        if (!_sessionReady) return;
        try
        {
            SessionStore.Save(SessionPath, new QueueSession
            {
                InputDirectory = _inputDirectory,
                OutputDirectory = OutputDirectoryText.Text.Trim(),
                ElapsedSeconds = QueueElapsed.TotalSeconds,
                Engine = (EngineCombo.SelectedItem as EngineOption)?.Code ?? "animejanai",
                Profile = (ProfileCombo.SelectedItem as IProcessingProfile)?.Code ?? "V3.1-Balanced-Sharp1",
                Cq = int.TryParse(CqText.Text, out var cq) ? cq : null,
                AqStrength10 = BetaAqStrengthCheck.IsChecked == true,
                BAdapt = BetaBAdaptCheck.IsChecked == true,
                AdvancedMode = AdvancedModeCheck.IsChecked == true,
                AdvancedCommand = AdvancedCommandText.Text,
                Items = Items.Select(item => new QueueEntry
                {
                    InputPath = item.InputPath, OutputPath = item.OutputPath, Status = item.Status,
                    Error = item.Error, LogPath = item.LogPath, Progress = item.Progress,
                    ElapsedSeconds = item.Elapsed.TotalSeconds, OutputBytes = item.ActualOutputBytes,
                    Media = new MediaInfo(item.Duration, item.InputBytes, item.InputBitRate, item.InputVideoCodec,
                        item.InputPixelFormat, item.InputWidth, item.InputHeight, item.HasAssSubtitle,
                        Enumerable.Repeat("", item.AudioTracks).ToArray(), item.EstimatedAudioOutputBitRate)
                }).ToList()
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FooterText.Text = "Não foi possível salvar a fila e as pastas: " + ex.Message;
        }
    }

    private static bool PendingItem(EncodeItem item) => item.Status is "Aguardando" or "Cancelado" or "Falhou";

    private async Task AddInputsAsync(IEnumerable<string> paths)
    {
        _addingFiles++;
        try
        {
            foreach (var path in paths)
            {
                if (Items.Any(item => string.Equals(item.InputPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                _inputDirectory = Path.GetDirectoryName(path) ?? _inputDirectory;
                var item = new EncodeItem(path);
                Items.Add(item);
                try
                {
                    item.ApplyMediaInfo(await ProbeMediaCoreAsync(path, CancellationToken.None, false));
                    item.Status = "Aguardando";
                    item.Compatibility = "Entrada analisada; integridade será verificada antes do upscale";
                }
                catch (Exception ex) { item.Status = "Erro ao analisar"; item.Error = ex.Message; }
                SaveSession();
            }
        }
        finally { _addingFiles--; UpdateEstimates(); RefreshOverall(); SaveSession(); }
    }

    private async Task RunQueueAsync(JobSettings settings, CancellationToken token)
    {
        var attempted = new HashSet<EncodeItem>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // A user-authored command can contain literal paths for a single input/output.
            if (attempted.Count > 0 && !string.IsNullOrWhiteSpace(settings.AdvancedFfmpegCommand)) return;
            var next = Items.FirstOrDefault(item => PendingItem(item) && !attempted.Contains(item));
            if (next is null)
            {
                if (_addingFiles > 0) { await Task.Delay(100, token); continue; }
                return;
            }
            attempted.Add(next);
            await EncodeInBackgroundAsync(next, settings, token);
            SaveSession();
        }
    }

    private void MaximumPerformance_Click(object sender, RoutedEventArgs e) => ResourceModeCombo.SelectedIndex = 2;
}
