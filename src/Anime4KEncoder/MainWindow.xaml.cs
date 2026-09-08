using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Anime4KEncoder;

public partial class MainWindow : Window
{
    private const int SegmentedPipelineVersion = 1;
    private const int AnimeJanaiSegmentSeconds = 300;
    private const int AnimeJanaiOverlapFrames = 2;
    private const int PilotPipelineVersion = 1;
    private const int ToolchainFingerprintVersion = 1;

    public ObservableCollection<EncodeItem> Items { get; } = [];

    private readonly string _root;
    private readonly string _dataRoot;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _shaderDirectory;
    private readonly string _modelDirectory;
    private readonly string _inferenceDirectory;
    private readonly string _ajiEncode;
    private readonly string _trtexec;
    private readonly string _historyPath;
    private readonly string _betaHistoryPath;
    private readonly string _logsDirectory;
    private readonly string _fileHashCachePath;
    private readonly string _toolchainSnapshotsDirectory;
    private readonly string _pilotCacheDirectory;
    private readonly List<EngineOption> _engines = [];
    private readonly List<ShaderPreset> _shaderPresets = [];
    private readonly List<AnimeJanaiPreset> _animeJanaiPresets = [];
    private readonly List<HistoryRecord> _history = [];
    private readonly List<PilotRunResult> _pilotHistory = [];
    private readonly Dictionary<string, FileHashCacheEntry> _fileHashCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _verifiedFileHashesThisSession = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _historyLock = new();
    private readonly SemaphoreSlim _fingerprintGate = new(1, 1);
    private CancellationTokenSource? _queueCancellation;
    private readonly DispatcherTimer _elapsedTimer;
    private DateTimeOffset? _queueStartedAt;
    private TimeSpan? _queueElapsed;
    private bool _environmentReady;
    private EncodeItem? _activePilotItem;
    private PilotRunResult? _lastPilotResult;
    private string _activeToolchainFingerprint = "";
    private CancellationTokenSource? _pilotCacheRefreshCancellation;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        BetaLookaheadCheck.IsEnabled = false;
        BetaLookaheadCheck.Content = "Lookahead 4 (indisponível)";
        BetaLookaheadCheck.ToolTip = "A build/driver atual do av1_nvenc aceita no máximo lookahead_level 3, que já é usado pelo modo estável.";
        BetaBRefCheck.IsEnabled = false;
        BetaBRefCheck.Content = "B-frames como referência (indisponível)";
        BetaBRefCheck.ToolTip = "O av1_nvenc atual falhou ao inicializar com b_ref_mode hierarchical nesta GPU/driver.";
        BetaWeightedPredCheck.IsEnabled = false;
        BetaWeightedPredCheck.Content = "Weighted prediction (indisponível)";
        BetaWeightedPredCheck.ToolTip = "O av1_nvenc atual informa que weighted prediction não é suportado para esta saída AV1.";
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var now = DateTimeOffset.Now;
            foreach (var item in Items) item.UpdateElapsed(now);
            RefreshOverall();
        };

        _root = FindProjectRoot();
        _dataRoot = StoragePaths.DataRoot(_root);
        _ffmpeg = Path.Combine(_root, "tools", "ffmpeg", "ffmpeg.exe");
        _ffprobe = Path.Combine(_root, "tools", "ffmpeg", "ffprobe.exe");
        _shaderDirectory = Path.Combine(_root, "shaders");
        _modelDirectory = StoragePaths.ModelRoot(_root, _dataRoot);
        _inferenceDirectory = Path.Combine(_root, "tools", "videojanai", "inference");
        _ajiEncode = Path.Combine(_inferenceDirectory, "aji_encode.exe");
        _trtexec = Path.Combine(_inferenceDirectory, "trtexec.exe");
        _historyPath = Path.Combine(_dataRoot, "data", "encoding-history.json");
        _betaHistoryPath = Path.Combine(_dataRoot, "data", "encoding-history-beta.json");
        _logsDirectory = Path.Combine(_dataRoot, "logs");
        _fileHashCachePath = Path.Combine(_dataRoot, "data", "toolchain-file-hashes.json");
        _toolchainSnapshotsDirectory = Path.Combine(_dataRoot, "data", "toolchain-snapshots");
        _pilotCacheDirectory = Path.Combine(_dataRoot, "validation", "pilots");

        LoadHistory();
        LoadFileHashCache();
        LoadPilotHistory();
        LoadProfiles();
        ValidateEnvironment();
        Loaded += async (_, _) => await CheckPortableEnvironmentAsync();
        InitializeUpdates();
    }

    private static string FindProjectRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "shaders")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "tools", "ffmpeg")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }

        return AppContext.BaseDirectory;
    }

    private async Task CheckPortableEnvironmentAsync()
    {
        if (!_environmentReady) return;
        _environmentReady = false;
        StartButton.IsEnabled = PilotButton.IsEnabled = false;
        ToolsStatusText.Text = "Verificando driver e encoder NVIDIA…";
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            var probe = Path.Combine(_dataRoot, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            await CheckNativeToolAsync(_trtexec, ["--help"]);
            await CheckNativeToolAsync(_ffmpeg, ["-hide_banner", "-nostdin", "-v", "error",
                "-f", "lavfi", "-i", "color=s=640x360:r=1", "-frames:v", "1",
                "-c:v", "av1_nvenc", "-pix_fmt", "p010le", "-f", "null", "-"]);
            _environmentReady = true;
            StartButton.IsEnabled = PilotButton.IsEnabled = true;
            ToolsStatusText.Text = "● NVIDIA AV1 e TensorRT disponíveis";
            FooterText.Text = "Primeiro uso do AnimeJaNai: a engine será preparada para sua GPU. Comece pelo piloto de 15 segundos.";
        }
        catch (Exception ex)
        {
            ToolsStatusText.Text = "Não foi possível preparar o processamento";
            ToolsStatusText.Foreground = new SolidColorBrush(Color.FromRgb(247, 108, 108));
            FooterText.Text = "Extraia toda a pasta para um local gravável e atualize o driver NVIDIA. Consulte logs/startup.txt.";
            try { File.WriteAllText(Path.Combine(_logsDirectory, "startup.txt"), ex.ToString()); } catch { }
            MessageBox.Show(this, "Não foi possível iniciar as ferramentas. Extraia o ZIP inteiro para uma pasta sua e instale o driver NVIDIA mais recente.\n\n" + ex.Message,
                "Verificação do computador", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task CheckNativeToolAsync(string executable, IEnumerable<string> arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var process = Process.Start(CreateProcessInfo(executable, arguments, Path.GetDirectoryName(executable)!))
            ?? throw new InvalidOperationException("Não foi possível abrir " + Path.GetFileName(executable));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("A verificação de " + Path.GetFileName(executable) + " excedeu 45 segundos.");
        }
        var output = await stdout + "\n" + await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(Path.GetFileName(executable) + " (" + process.ExitCode + "): " + output);
    }

    private void LoadProfiles()
    {
        _engines.Add(new("Anime4K", "anime4k"));
        _engines.Add(new("AnimeJaNai", "animejanai"));

        _shaderPresets.Add(new("Anime4K A+A (HQ)", "A+A", [
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl",
            "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_Restore_CNN_M.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_M.glsl"]));
        _shaderPresets.Add(new("Anime4K A (HQ)", "A", [
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_VL.glsl",
            "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]));
        _shaderPresets.Add(new("Anime4K B (HQ suave)", "B", [
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Restore_CNN_Soft_VL.glsl",
            "Anime4K_Upscale_CNN_x2_VL.glsl", "Anime4K_AutoDownscalePre_x2.glsl",
            "Anime4K_AutoDownscalePre_x4.glsl", "Anime4K_Upscale_CNN_x2_M.glsl"]));
        _shaderPresets.Add(new("Anime4K C (HQ denoise)", "C", [
            "Anime4K_Clamp_Highlights.glsl", "Anime4K_Upscale_Denoise_CNN_x2_VL.glsl",
            "Anime4K_AutoDownscalePre_x2.glsl", "Anime4K_AutoDownscalePre_x4.glsl",
            "Anime4K_Upscale_CNN_x2_M.glsl"]));

        _animeJanaiPresets.Add(new(
            "V3 Compact FP16 (Legacy/Experimental)", "V3-Compact",
            "2x_AnimeJaNai_HD_V3_Compact_strong_fp16",
            true,
            "Conversão FP16 forte validada localmente em 120 quadros 4K sem corrupção; o ONNX FP32 original não é usado. Perfil legado/experimental até passar por piloto longo e comparação de qualidade."));
        _animeJanaiPresets.Add(new(
            "V3 Compact Sharp1 FP16 (Legacy/Experimental)", "V3-Compact-Sharp1",
            "2x_AnimeJaNai_HD_V3Sharp1_Compact_strong_fp16",
            true,
            "Conversão FP16 forte validada localmente em 120 quadros 4K sem corrupção; o ONNX FP32 original não é usado. Perfil legado/experimental até passar por piloto longo e comparação de qualidade."));
        _animeJanaiPresets.Add(new(
            "V3.1 Balanced", "V3.1-Balanced",
            "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16"));
        _animeJanaiPresets.Add(new(
            "V3.1 Balanced Sharp1", "V3.1-Balanced-Sharp1",
            "2x_AnimeJaNai_HD_V3.1Sharp1_Balanced_SPANF3_b8f64_unshuffle_fp16"));
        _animeJanaiPresets.Add(new(
            "V3.1 Performance", "V3.1-Performance",
            "2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16"));
        _animeJanaiPresets.Add(new(
            "V3.1 Performance Sharp1", "V3.1-Performance-Sharp1",
            "2x_AnimeJaNai_HD_V3.1Sharp1_Performance_SPANF3_b5f48_unshuffle_fp16"));

        EngineCombo.ItemsSource = _engines;
        EngineCombo.SelectedIndex = 0;
    }

    private void ValidateEnvironment()
    {
        var missing = new List<string>();
        if (!File.Exists(_ffmpeg)) missing.Add("ffmpeg.exe");
        if (!File.Exists(_ffprobe)) missing.Add("ffprobe.exe");
        if (!Directory.Exists(_shaderDirectory)) missing.Add("pasta shaders");
        if (!File.Exists(_ajiEncode)) missing.Add("aji_encode.exe");
        if (!File.Exists(_trtexec)) missing.Add("trtexec.exe");
        if (!Directory.Exists(_modelDirectory)) missing.Add("modelos AnimeJaNai");

        foreach (var preset in _animeJanaiPresets)
        {
            if (!File.Exists(Path.Combine(_modelDirectory, preset.ModelName + ".onnx")))
                missing.Add(preset.ModelName + ".onnx");
        }

        var ready = missing.Count == 0;
        _environmentReady = ready;
        ToolsStatusText.Text = ready ? "● Anime4K e AnimeJaNai prontos" : "Faltando: " + string.Join(", ", missing.Distinct());
        ToolsStatusText.Foreground = new SolidColorBrush(ready ? Color.FromRgb(91, 214, 145) : Color.FromRgb(247, 108, 108));
        StartButton.IsEnabled = ready;
        PilotButton.IsEnabled = ready;
        FooterText.Text = ready
            ? "AnimeJaNai usa blocos recuperáveis de 5 min isolados por fingerprint da toolchain; o original nunca é alterado."
            : $"Projeto: {_root}";
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || EngineCombo.SelectedItem is not EngineOption engine) return;

        if (engine.Code == "animejanai")
        {
            ProfileLabel.Text = "Modelo AnimeJaNai";
            ProfileCombo.ItemsSource = _animeJanaiPresets;
            ProfileCombo.SelectedIndex = _animeJanaiPresets.FindIndex(p => p.Code == "V3.1-Balanced");
            ConcurrencyCombo.SelectedIndex = 0;
            ConcurrencyCombo.IsEnabled = false;
            CompatibilitySummaryText.Text = "TensorRT → blocos recuperáveis de 5 min → AV1 10-bit. Áudio, legendas, capítulos e anexos vêm do original no remux final.";
        }
        else
        {
            ProfileLabel.Text = "Preset / shader Anime4K";
            ProfileCombo.ItemsSource = _shaderPresets;
            ProfileCombo.SelectedIndex = 0;
            ConcurrencyCombo.IsEnabled = _queueCancellation is null;
            CompatibilitySummaryText.Text = "AV1 Main 10-bit, 4K, MKV e AAC 48 kHz. Legendas ASS podem exigir transcodificação.";
        }

        UpdateProfileRiskNotice();
        UpdateEstimates();
        UpdateAdvancedCommandPreview();
        SchedulePilotCacheRefresh();
    }

    private void ProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateProfileRiskNotice();
        UpdateEstimates();
        UpdateAdvancedCommandPreview();
        SchedulePilotCacheRefresh();
    }

    private void UpdateProfileRiskNotice()
    {
        if (EngineCombo.SelectedItem is EngineOption { Code: "animejanai" } &&
            ProfileCombo.SelectedItem is AnimeJanaiPreset { IsLegacyExperimental: true } preset)
        {
            LegacyProfileWarningText.Text = preset.Warning;
            LegacyProfileWarningPanel.Visibility = Visibility.Visible;
            return;
        }

        LegacyProfileWarningPanel.Visibility = Visibility.Collapsed;
    }
    private void Settings_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateEstimates();
        UpdateOutputDiskSpace();
        UpdateAdvancedCommandPreview();
        SchedulePilotCacheRefresh();
    }

    private void AdvancedMode_Click(object sender, RoutedEventArgs e)
    {
        AdvancedPanel.Visibility = AdvancedModeCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (AdvancedModeCheck.IsChecked == true) UpdateAdvancedCommandPreview();
        UpdateEstimates();
        SchedulePilotCacheRefresh();
    }

    private void BetaMode_Click(object sender, RoutedEventArgs e)
    {
        BetaPanel.Visibility = BetaModeCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateEstimates();
        UpdateAdvancedCommandPreview();
        SchedulePilotCacheRefresh();
    }

    private void BetaOption_Click(object sender, RoutedEventArgs e)
    {
        if (BetaModeCheck.IsChecked == true)
        {
            UpdateEstimates();
            UpdateAdvancedCommandPreview();
            SchedulePilotCacheRefresh();
        }
    }

    private void AdvancedCommandText_Changed(object sender, TextChangedEventArgs e) { }

    private void UpdateAdvancedCommandPreview()
    {
        if (AdvancedModeCheck?.IsChecked != true || !IsInitialized) return;
        if (Items.Count == 0 || EngineCombo.SelectedItem is not EngineOption engine || ProfileCombo.SelectedItem is not IProcessingProfile profile)
        {
            AdvancedCommandText.Text = "Adicione um arquivo e selecione o motor para gerar o comando.";
            return;
        }

        var cq = int.TryParse(CqText.Text, out var parsed) ? Math.Clamp(parsed, 0, 63) : 28;
        string? shaderPath = null;
        if (engine.Code == "anime4k" && profile is ShaderPreset shaderPreset)
            shaderPath = BuildCombinedShader(shaderPreset);

        var args = BuildFfmpegArguments("{INPUT}", "{OUTPUT}", cq, shaderPath);
        args = ApplyBetaOptions(args, BetaModeCheck.IsChecked == true ? ReadBetaOptions() : new BetaFfmpegOptions());
        var generated = FormatCommand(_ffmpeg, args);
        if (string.IsNullOrWhiteSpace(AdvancedCommandText.Text) || AdvancedCommandText.Tag is not string currentGenerated || currentGenerated == AdvancedCommandText.Text)
            AdvancedCommandText.Text = generated;
        AdvancedCommandText.Tag = generated;
    }

    private BetaFfmpegOptions ReadBetaOptions() => new(
        BetaLookaheadCheck.IsEnabled && BetaLookaheadCheck.IsChecked == true,
        BetaAqStrengthCheck.IsChecked == true,
        BetaBAdaptCheck.IsChecked == true,
        BetaBRefCheck.IsEnabled && BetaBRefCheck.IsChecked == true,
        BetaWeightedPredCheck.IsEnabled && BetaWeightedPredCheck.IsChecked == true);

    private static string EffectiveBetaSignature(JobSettings settings) =>
        settings.IsBeta ? settings.BetaOptions?.Signature ?? "" : "";

    private bool TryBuildCurrentFingerprintSettings(out JobSettings settings)
    {
        settings = default!;
        if (!IsInitialized || AdvancedModeCheck.IsChecked == true ||
            EngineCombo.SelectedItem is not EngineOption engine ||
            ProfileCombo.SelectedItem is not IProcessingProfile profile ||
            !int.TryParse(CqText.Text, out var cq) || cq is < 0 or > 63)
            return false;

        string? shaderPath = null;
        if (engine.Code == "anime4k")
        {
            if (profile is not ShaderPreset shaderPreset ||
                shaderPreset.Files.Any(file => !File.Exists(Path.Combine(_shaderDirectory, file))))
                return false;
            shaderPath = BuildCombinedShader(shaderPreset);
        }
        var betaOptions = BetaModeCheck.IsChecked == true ? ReadBetaOptions() : new BetaFfmpegOptions();
        settings = new JobSettings(
            engine.Code,
            profile.Code,
            profile.Name,
            cq,
            engine.Code == "animejanai" ? 1 : ConcurrencyCombo.SelectedIndex + 1,
            OutputDirectoryText.Text.Trim(),
            shaderPath,
            profile is AnimeJanaiPreset animeJanai ? animeJanai.ModelName : null,
            null,
            BetaModeCheck.IsChecked == true,
            betaOptions);
        return true;
    }

    private void SchedulePilotCacheRefresh()
    {
        if (!IsInitialized || string.IsNullOrWhiteSpace(_pilotCacheDirectory) || _queueCancellation is not null) return;
        _pilotCacheRefreshCancellation?.Cancel();
        _activeToolchainFingerprint = "";
        UpdateEstimates();
        _pilotCacheRefreshCancellation = new CancellationTokenSource();
        _ = RefreshPilotCacheAfterDelayAsync(_pilotCacheRefreshCancellation.Token);
    }

    private async Task RefreshPilotCacheAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            if (!TryBuildCurrentFingerprintSettings(out var settings) || Items.Count == 0) return;
            var baseSignatures = Items.Select(item => BuildPilotSignature(
                    item,
                    settings.Engine,
                    settings.ProfileCode,
                    settings.Cq,
                    settings.IsBeta,
                    EffectiveBetaSignature(settings)))
                .Where(signature => !string.IsNullOrWhiteSpace(signature))
                .ToHashSet(StringComparer.Ordinal);
            var candidates = _pilotHistory
                .Where(result => baseSignatures.Contains(result.Signature) &&
                                 !string.IsNullOrWhiteSpace(result.ToolchainFingerprint) &&
                                 File.Exists(result.PreviewPath))
                .OrderByDescending(result => result.CreatedAt)
                .ToArray();
            if (candidates.Length == 0) return;

            var snapshot = await ComputeToolchainSnapshotAsync(settings, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var match = candidates.FirstOrDefault(result => result.ToolchainFingerprint == snapshot.Hash);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                _activeToolchainFingerprint = snapshot.Hash;
                _lastPilotResult = match;
                UpdateEstimates();
            });
        }
        catch (OperationCanceledException) { }
        catch
        {
            // Falhas de leitura do cache não devem impedir o uso normal do Studio.
        }
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Vídeos MKV|*.mkv|Vídeos compatíveis|*.mkv;*.mp4;*.webm|Todos os arquivos|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        foreach (var path in dialog.FileNames.Where(p => Items.All(i => !string.Equals(i.InputPath, p, StringComparison.OrdinalIgnoreCase))))
        {
            var item = new EncodeItem(path);
            Items.Add(item);
            try
            {
                var media = await ProbeMediaAsync(path);
                item.ApplyMediaInfo(media);
                item.Status = "Aguardando";
                item.Compatibility = media.Width == 1920 && media.Height == 1080
                    ? "Entrada adequada"
                    : $"Revisar {media.Width}×{media.Height}";
            }
            catch (Exception ex)
            {
                item.Status = "Erro ao analisar";
                item.Error = ex.Message;
                item.Compatibility = "Não analisado";
            }
        }

        UpdateEstimates();
        RefreshOverall();
        SchedulePilotCacheRefresh();
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in QueueGrid.SelectedItems.Cast<EncodeItem>().Where(i => !i.IsRunning).ToArray())
            Items.Remove(item);
        UpdateEstimates();
        RefreshOverall();
        SchedulePilotCacheRefresh();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items.Where(i => !i.IsRunning).ToArray()) Items.Remove(item);
        UpdateEstimates();
        RefreshOverall();
        SchedulePilotCacheRefresh();
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta de saída", Multiselect = false };
        if (dialog.ShowDialog() == true) OutputDirectoryText.Text = dialog.FolderName;
    }

    private void UpdateOutputDiskSpace(string? directory = null)
    {
        var requestedPath = string.IsNullOrWhiteSpace(directory) ? OutputDirectoryText.Text.Trim() : directory.Trim();
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            OutputDiskSpaceText.Text = "Espaço livre: selecione uma pasta ou adicione arquivos";
            OutputDiskSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(153, 168, 189));
            return;
        }

        try
        {
            var path = Path.GetFullPath(requestedPath);
            var existingPath = path;
            while (!Directory.Exists(existingPath))
            {
                var parent = Directory.GetParent(existingPath);
                if (parent is null) break;
                existingPath = parent.FullName;
            }

            var drive = new DriveInfo(Path.GetPathRoot(existingPath)!);
            var free = drive.AvailableFreeSpace;
            OutputDiskSpaceText.Text = Directory.Exists(path)
                ? $"Espaço livre: {FormatBytes(free)}"
                : $"Espaço livre: {FormatBytes(free)} (a pasta será criada)";
            OutputDiskSpaceText.Foreground = new SolidColorBrush(free < 10L * 1024 * 1024 * 1024
                ? Color.FromRgb(247, 199, 106)
                : Color.FromRgb(153, 168, 189));
        }
        catch
        {
            OutputDiskSpaceText.Text = "Espaço livre: não foi possível consultar a pasta";
            OutputDiskSpaceText.Foreground = new SolidColorBrush(Color.FromRgb(247, 108, 108));
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0)
        {
            MessageBox.Show("Adicione pelo menos um arquivo.", "Fila vazia", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (EngineCombo.SelectedItem is not EngineOption engine || ProfileCombo.SelectedItem is not IProcessingProfile profile) return;
        if (!int.TryParse(CqText.Text, out var cq) || cq is < 0 or > 63)
        {
            MessageBox.Show("CQ deve estar entre 0 e 63.", "Valor inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pending = Items.Where(i => i.Status is "Aguardando" or "Cancelado" or "Falhou").ToArray();
        if (pending.Length == 0)
        {
            MessageBox.Show("Não há itens pendentes.", "Fila", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (profile is AnimeJanaiPreset { IsLegacyExperimental: true } legacyPreset)
        {
            var confirmation = MessageBox.Show(
                $"{legacyPreset.Name}\n\n{legacyPreset.Warning}\n\n" +
                "A variante FP16 é utilizável no pipeline atual, mas ainda não foi validada em um episódio completo. " +
                "Para o perfil com maior maturidade, escolha V3.1 Balanced ou V3.1 Performance.\n\nDeseja iniciar o perfil experimental?",
                "Perfil AnimeJaNai Legacy/Experimental",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes) return;
        }

        string? shaderPath = null;
        if (engine.Code == "anime4k")
        {
            var shaderPreset = (ShaderPreset)profile;
            var missingShaders = shaderPreset.Files.Where(f => !File.Exists(Path.Combine(_shaderDirectory, f))).ToArray();
            if (missingShaders.Length > 0)
            {
                MessageBox.Show("Shaders ausentes:\n" + string.Join("\n", missingShaders), "Ambiente incompleto", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            shaderPath = BuildCombinedShader(shaderPreset);
        }

        if (AdvancedModeCheck.IsChecked == true && pending.Length != 1)
        {
            MessageBox.Show("O Modo avançado exige exatamente um item pendente, pois o comando exibido representa um arquivo específico.", "Modo avançado", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = new JobSettings(
            engine.Code,
            profile.Code,
            profile.Name,
            cq,
            engine.Code == "animejanai" ? 1 : ConcurrencyCombo.SelectedIndex + 1,
            OutputDirectoryText.Text.Trim(),
            shaderPath,
            profile is AnimeJanaiPreset animeJanai ? animeJanai.ModelName : null,
            AdvancedModeCheck.IsChecked == true ? AdvancedCommandText.Text.Trim() : null,
            BetaModeCheck.IsChecked == true,
            BetaModeCheck.IsChecked == true ? ReadBetaOptions() : new BetaFfmpegOptions());

        var concurrency = engine.Code == "animejanai" ? 1 : ConcurrencyCombo.SelectedIndex + 1;
        _queueCancellation = new CancellationTokenSource();
        SetRunningUi(true);
        _queueStartedAt = DateTimeOffset.Now;
        _queueElapsed = TimeSpan.Zero;
        _elapsedTimer.Start();
        using var gate = new SemaphoreSlim(concurrency);

        try
        {
            await Task.WhenAll(pending.Select(async item =>
            {
                await gate.WaitAsync(_queueCancellation.Token);
                try { await EncodeAsync(item, settings, _queueCancellation.Token); }
                finally { gate.Release(); }
            }));
        }
        catch (OperationCanceledException) { }
        finally
        {
            _elapsedTimer.Stop();
            if (_queueStartedAt.HasValue) _queueElapsed = DateTimeOffset.Now - _queueStartedAt.Value;
            SetRunningUi(false);
            RefreshOverall();
            _queueCancellation.Dispose();
            _queueCancellation = null;

            var failures = Items.Where(i => i.Status == "Falhou").ToArray();
            if (failures.Length > 0)
            {
                var details = string.Join("\n\n", failures.Select(i => $"{i.FileName}\nEtapa: {i.Stage}\n{i.Error}\nLog: {i.LogPath}"));
                if (details.Length > 7000) details = details[..7000] + "\n…";
                MessageBox.Show(details, "Falha no processamento", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async void Pilot_Click(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0)
        {
            MessageBox.Show("Adicione pelo menos um arquivo para medir o piloto.", "Piloto", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (AdvancedModeCheck.IsChecked == true)
        {
            MessageBox.Show(
                "O piloto não é executado no Modo avançado, porque um comando arbitrário não pode ser recortado sem alterar seu significado. Desative o Modo avançado para medir o pipeline padrão.",
                "Piloto indisponível",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (EngineCombo.SelectedItem is not EngineOption engine || ProfileCombo.SelectedItem is not IProcessingProfile profile) return;
        if (!int.TryParse(CqText.Text, out var cq) || cq is < 0 or > 63)
        {
            MessageBox.Show("CQ deve estar entre 0 e 63.", "Valor inválido", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var target = QueueGrid.SelectedItems.Cast<EncodeItem>().FirstOrDefault()
                     ?? Items.FirstOrDefault(item => item.Duration > TimeSpan.Zero)
                     ?? Items[0];
        string? shaderPath = null;
        if (engine.Code == "anime4k")
        {
            var shaderPreset = (ShaderPreset)profile;
            var missingShaders = shaderPreset.Files.Where(file => !File.Exists(Path.Combine(_shaderDirectory, file))).ToArray();
            if (missingShaders.Length > 0)
            {
                MessageBox.Show("Shaders ausentes:\n" + string.Join("\n", missingShaders), "Ambiente incompleto", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            shaderPath = BuildCombinedShader(shaderPreset);
        }

        var betaOptions = BetaModeCheck.IsChecked == true ? ReadBetaOptions() : new BetaFfmpegOptions();
        var settings = new JobSettings(
            engine.Code,
            profile.Code,
            profile.Name,
            cq,
            1,
            OutputDirectoryText.Text.Trim(),
            shaderPath,
            profile is AnimeJanaiPreset animeJanai ? animeJanai.ModelName : null,
            null,
            BetaModeCheck.IsChecked == true,
            betaOptions);

        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var safeBaseName = SafeBaseName(Path.GetFileNameWithoutExtension(target.InputPath), 70);
        var pilotDirectory = Path.Combine(_pilotCacheDirectory, $"{jobId}-{Sanitize(settings.ProfileCode)}");
        var workDirectory = Path.Combine(pilotDirectory, "work");
        var previewPath = Path.Combine(pilotDirectory, $"{safeBaseName} [Piloto {settings.ProfileCode} CQ{settings.Cq}].mkv");
        var previewPartialPath = previewPath + ".partial.mkv";
        var reportPath = Path.Combine(pilotDirectory, "pilot-report.json");
        var logPath = CreateLogPath(safeBaseName + "-piloto", jobId);
        var pilotItem = new EncodeItem(target.InputPath)
        {
            IsRunning = true,
            Status = "Preparando piloto",
            Stage = "Inventário da entrada",
            StartedAt = DateTimeOffset.Now,
            LogPath = logPath
        };
        var preparedPaths = new List<string>();
        var completedSuccessfully = false;

        _queueCancellation = new CancellationTokenSource();
        _activePilotItem = pilotItem;
        _lastPilotResult = null;
        _activeToolchainFingerprint = "";
        SetRunningUi(true);
        PilotSummaryPanel.Visibility = Visibility.Visible;
        OpenPilotButton.IsEnabled = false;
        ComparePilotButton.IsEnabled = false;
        PilotSummaryText.Text = $"Medindo {target.FileName} com {settings.ProfileName} em CQ {settings.Cq}…";
        RefreshOverall();

        try
        {
            var cancellationToken = _queueCancellation.Token;
            Directory.CreateDirectory(workDirectory);
            var media = await ProbeMediaAsync(target.InputPath, cancellationToken);
            pilotItem.ApplyMediaInfo(media);
            await Dispatcher.InvokeAsync(() => target.ApplyMediaInfo(media));

            var source = await ProbeMediaStructureAsync(target.InputPath, true, cancellationToken);
            if (source.VideoPackets <= 0 || source.AverageFrameRate <= 0)
                throw new InvalidOperationException("Não foi possível determinar a quantidade de frames e o FPS da entrada.");
            if (source.NominalFrameRate > 0 && Math.Abs(source.NominalFrameRate - source.AverageFrameRate) / source.AverageFrameRate > 0.001)
                throw new InvalidOperationException("A entrada parece usar frame rate variável. O piloto exige vídeo CFR para medir trechos exatos.");

            var samples = PilotPlanner.BuildSamples(source.VideoPackets, source.AverageFrameRate);
            AppendLog(logPath,
                $"[{DateTime.Now:O}] Piloto v{PilotPipelineVersion}\n" +
                $"Entrada={target.InputPath}\nMotor={settings.Engine}\nPerfil={settings.ProfileCode}\nCQ={settings.Cq}\n" +
                $"FPS={source.AverageFrameRate:0.########}\nFrames={source.VideoPackets}\n" +
                $"Amostras={string.Join(", ", samples.Select(sample => $"#{sample.Index + 1}@{sample.StartFrame}+{sample.FrameCount}"))}\n");

            const double prepareSpan = 16;
            for (var index = 0; index < samples.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = samples[index];
                var samplePath = Path.Combine(workDirectory, $"source-{sample.Index + 1:00}.mkv");
                preparedPaths.Add(samplePath);
                var sampleDuration = TimeSpan.FromSeconds(sample.FrameCount / source.AverageFrameRate);
                var arguments = BuildLosslessSourceSegmentArguments(
                    target.InputPath,
                    samplePath,
                    sample.StartFrame / source.AverageFrameRate,
                    sample.FrameCount,
                    source.PixelFormat);
                pilotItem.Stage = $"Preparando amostra {index + 1}/{samples.Count}";
                await RunFfmpegAsync(
                    pilotItem,
                    arguments,
                    index * prepareSpan / samples.Count,
                    prepareSpan / samples.Count,
                    logPath,
                    cancellationToken,
                    sampleDuration,
                    $"Preparando amostra {index + 1}/{samples.Count}");

                var prepared = await ProbeMediaStructureAsync(samplePath, true, cancellationToken);
                if (prepared.VideoPackets != sample.FrameCount || prepared.AudioStreams != 0 || prepared.SubtitleStreams != 0 || prepared.AttachmentStreams != 0)
                    throw new InvalidOperationException($"A amostra {index + 1} não preservou a contagem exata de frames ou contém faixas inesperadas.");
            }

            var concatPath = Path.Combine(workDirectory, "pilot-source.ffconcat");
            var pilotSourcePath = Path.Combine(workDirectory, "pilot-source.mkv");
            preparedPaths.Add(concatPath);
            preparedPaths.Add(pilotSourcePath);
            WritePilotConcatManifest(concatPath, preparedPaths.Where(path => Path.GetFileName(path).StartsWith("source-", StringComparison.OrdinalIgnoreCase)).ToArray());
            var concatArguments = BuildPilotConcatArguments(concatPath, pilotSourcePath);
            var sampledFrames = samples.Sum(sample => sample.FrameCount);
            var sampledDuration = sampledFrames / source.AverageFrameRate;
            await RunFfmpegAsync(
                pilotItem,
                concatArguments,
                prepareSpan,
                4,
                logPath,
                cancellationToken,
                TimeSpan.FromSeconds(sampledDuration),
                "Montando amostra representativa");
            var combined = await ProbeMediaStructureAsync(pilotSourcePath, true, cancellationToken);
            if (combined.VideoPackets != sampledFrames || combined.AudioStreams != 0 || combined.SubtitleStreams != 0 || combined.AttachmentStreams != 0)
                throw new InvalidOperationException("A amostra consolidada não preservou a contagem exata de frames.");

            var coreProgressStart = 20d;
            string? configPath = null;
            if (settings.Engine == "animejanai")
            {
                if (string.IsNullOrWhiteSpace(settings.ModelName)) throw new InvalidOperationException("Modelo AnimeJaNai não selecionado.");
                var modelPath = Path.Combine(_modelDirectory, settings.ModelName + ".onnx");
                if (!File.Exists(modelPath)) throw new FileNotFoundException("Modelo AnimeJaNai não encontrado.", modelPath);
                configPath = WriteAnimeJanaiConfig(workDirectory, settings.ModelName);
                preparedPaths.Add(configPath);
                await PrepareTensorRtAsync(pilotItem, pilotSourcePath, configPath, 20, 10, logPath, cancellationToken);
                coreProgressStart = 30;
            }

            pilotItem.Stage = "Calculando fingerprint da toolchain";
            var toolchain = await ComputeToolchainSnapshotAsync(settings, cancellationToken);
            _activeToolchainFingerprint = toolchain.Hash;
            AppendLog(logPath, $"\nFingerprint toolchain v{toolchain.Version}: {toolchain.Hash}\nComponentes={toolchain.Components.Count}\nAmbiente={toolchain.EnvironmentSignature}\n");

            TryDelete(previewPartialPath);
            var coreStopwatch = Stopwatch.StartNew();
            if (settings.Engine == "animejanai")
            {
                var segment = new AnimeJanaiSegmentPlan(0, 0, sampledFrames, 0, sampledFrames, 0);
                await RunAnimeJanaiSegmentPipeAsync(
                    pilotItem,
                    settings,
                    pilotSourcePath,
                    previewPartialPath,
                    configPath!,
                    segment,
                    1,
                    coreProgressStart,
                    65,
                    source.AverageFrameRate,
                    logPath,
                    cancellationToken);
            }
            else
            {
                var arguments = BuildFfmpegArguments(pilotSourcePath, previewPartialPath, settings.Cq, settings.ShaderPath);
                arguments = ApplyBetaOptions(arguments, settings.BetaOptions ?? new BetaFfmpegOptions());
                await RunFfmpegAsync(
                    pilotItem,
                    arguments,
                    coreProgressStart,
                    65,
                    logPath,
                    cancellationToken,
                    TimeSpan.FromSeconds(sampledDuration),
                    "Anime4K + AV1 do piloto");
            }
            coreStopwatch.Stop();

            pilotItem.Stage = "Validando piloto";
            pilotItem.Progress = 96;
            RefreshOverall();
            await ValidateVideoSegmentAsync(previewPartialPath, sampledFrames, cancellationToken,
                settings.Engine == "animejanai" ? checked(source.Width * 2) : 3840,
                settings.Engine == "animejanai" ? checked(source.Height * 2) : 2160);
            var videoPacketBytes = await ProbeVideoPacketBytesAsync(previewPartialPath, cancellationToken);
            File.Move(previewPartialPath, previewPath, true);

            var metrics = PilotPlanner.CalculateProjection(
                settings.Engine,
                sampledDuration,
                coreStopwatch.Elapsed.TotalSeconds,
                videoPacketBytes > 0 ? videoPacketBytes : new FileInfo(previewPath).Length,
                target.Duration.TotalSeconds,
                target.EstimatedAudioOutputBitRate);
            var signature = BuildPilotSignature(target, settings.Engine, settings.ProfileCode, settings.Cq, settings.IsBeta, EffectiveBetaSignature(settings));
            PilotComparisonResult? comparison = null;
            try
            {
                pilotItem.Stage = "Gerando comparação visual dos mesmos quadros";
                comparison = await CreatePilotComparisonAsync(pilotSourcePath, previewPath, samples, source.AverageFrameRate,
                    settings.Engine == "animejanai" ? checked(source.Width * 2) : 3840,
                    settings.Engine == "animejanai" ? checked(source.Height * 2) : 2160,
                    settings.ProfileName, logPath, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppendLog(logPath, $"\nComparação visual indisponível; medição do piloto preservada.\n{ex}\n"); }
            var result = new PilotRunResult(
                PilotPipelineVersion,
                signature,
                toolchain.Hash,
                target.InputPath,
                settings.Engine,
                settings.ProfileCode,
                settings.ProfileName,
                settings.Cq,
                settings.IsBeta,
                EffectiveBetaSignature(settings),
                samples.Count,
                sampledDuration,
                coreStopwatch.Elapsed.TotalSeconds,
                metrics.SpeedFactor,
                metrics.VideoBitRate,
                target.EstimatedAudioOutputBitRate,
                metrics.ProjectedBitRate,
                metrics.ProjectedOutputBytes,
                metrics.ProjectedSeconds,
                metrics.MinimumProjectedSeconds,
                metrics.MaximumProjectedSeconds,
                previewPath,
                logPath,
                DateTimeOffset.Now,
                comparison);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            AppendLog(logPath, $"\nPiloto validado.\nRelatório={reportPath}\nAmostra={previewPath}\n{BuildPilotSummary(result)}\n");

            _lastPilotResult = result;
            _pilotHistory.RemoveAll(existing => existing.Signature == result.Signature && existing.ToolchainFingerprint == result.ToolchainFingerprint);
            _pilotHistory.Add(result);
            completedSuccessfully = true;
            pilotItem.Progress = 100;
            pilotItem.Stage = "Piloto concluído e validado";
            OpenPilotButton.IsEnabled = true;
            ShowPilotResult(result);
            UpdateEstimates();
            if (!SuppressInteractivePilotDialogs() && HasComparison(result))
                OpenPilotComparison();
            else if (!SuppressInteractivePilotDialogs())
                MessageBox.Show(
                    BuildPilotSummary(result) + "\n\nA amostra AV1 foi validada e pode ser aberta para inspeção visual.",
                    "Piloto concluído",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            PilotSummaryText.Text = "Piloto cancelado. Nenhuma projeção foi aplicada à fila.";
        }
        catch (Exception ex)
        {
            AppendLog(logPath, $"\nERRO NO PILOTO\n{ex}\n");
            PilotSummaryText.Text = "O piloto falhou e nenhuma projeção foi aplicada. Consulte o log para os detalhes.";
            if (!SuppressInteractivePilotDialogs())
                MessageBox.Show(FriendlyError(ex.Message) + $"\n\nLog: {logPath}", "Falha no piloto", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            TryDelete(previewPartialPath);
            foreach (var path in preparedPaths) TryDelete(path);
            TryDeleteDirectory(workDirectory);
            if (!completedSuccessfully) TryDeleteDirectory(pilotDirectory);
            pilotItem.IsRunning = false;
            pilotItem.FinishTiming(DateTimeOffset.Now);
            _activePilotItem = null;
            _queueCancellation?.Dispose();
            _queueCancellation = null;
            SetRunningUi(false);
            RefreshOverall();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _queueCancellation?.Cancel();

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_logsDirectory);
        Process.Start(new ProcessStartInfo("explorer.exe", _logsDirectory) { UseShellExecute = true });
    }

    private void OpenPilot_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPilotResult is null || !File.Exists(_lastPilotResult.PreviewPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastPilotResult.PreviewPath}\"") { UseShellExecute = true });
    }

    private static bool SuppressInteractivePilotDialogs() =>
        string.Equals(Environment.GetEnvironmentVariable("ANIME_UPSCALE_STUDIO_AUTOMATION"), "1", StringComparison.Ordinal);

    private string BuildCombinedShader(ShaderPreset preset)
    {
        var cache = Path.Combine(_dataRoot, "cache", "shaders");
        Directory.CreateDirectory(cache);
        var path = Path.Combine(cache, $"Anime4K_{Sanitize(preset.Code)}.glsl");
        var builder = new StringBuilder();
        foreach (var file in preset.Files)
        {
            builder.AppendLine($"// APPEND-BEGIN {file}");
            builder.AppendLine(File.ReadAllText(Path.Combine(_shaderDirectory, file)));
            builder.AppendLine($"// APPEND-END {file}");
        }
        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        return path;
    }

    private async Task EncodeAsync(EncodeItem item, JobSettings settings, CancellationToken cancellationToken)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            item.IsRunning = true;
            item.Status = "Preparando";
            item.Stage = "Preparação";
            item.Progress = 0;
            item.Processed = TimeSpan.Zero;
            item.Error = "";
            item.StartedAt = DateTimeOffset.Now;
        });

        var destinationDirectory = string.IsNullOrWhiteSpace(settings.OutputDirectory)
            ? Path.GetDirectoryName(item.InputPath)!
            : settings.OutputDirectory;
        Directory.CreateDirectory(destinationDirectory);
        await Dispatcher.InvokeAsync(() => UpdateOutputDiskSpace(destinationDirectory));

        var safeBaseName = SafeBaseName(Path.GetFileNameWithoutExtension(item.InputPath));
        var suffix = settings.Engine == "anime4k"
            ? $" [Anime4K {settings.ProfileCode} AV1 CQ{settings.Cq}]"
            : $" [AnimeJaNai {settings.ProfileCode} AV1 CQ{settings.Cq}]";
        var finalPath = Path.Combine(destinationDirectory, safeBaseName + suffix + ".mkv");
        var partialPath = Path.Combine(destinationDirectory, safeBaseName + suffix + ".partial.mkv");
        var jobId = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
        var useSegmentedAnimeJanai = settings.Engine == "animejanai" && string.IsNullOrWhiteSpace(settings.AdvancedFfmpegCommand);
        var workDirectory = useSegmentedAnimeJanai
            ? BuildSegmentedWorkDirectory(destinationDirectory, item, settings)
            : Path.Combine(destinationDirectory, ".anime-upscale-temp", jobId);
        var logPath = CreateLogPath(safeBaseName, jobId);
        item.LogPath = logPath;

        if (File.Exists(finalPath))
        {
            await Dispatcher.InvokeAsync(() =>
            {
                item.Status = "Saída já existe";
                item.Stage = "Não iniciado";
                item.IsRunning = false;
            });
            return;
        }

        TryDelete(partialPath);
        Directory.CreateDirectory(workDirectory);
        var stopwatch = Stopwatch.StartNew();
        MediaStructure? segmentedSource = null;

        try
        {
            if (settings.Engine == "anime4k")
                await RunAnime4KAsync(item, settings, partialPath, logPath, cancellationToken);
            else if (useSegmentedAnimeJanai)
                segmentedSource = await RunAnimeJanaiSegmentedAsync(item, settings, partialPath, workDirectory, logPath, cancellationToken);
            else
                await RunAnimeJanaiPipeAsync(item, settings, partialPath, workDirectory, logPath, cancellationToken);

            await Dispatcher.InvokeAsync(() => { item.Status = "Validando"; item.Stage = "Validação Jellyfin"; });
            var validation = segmentedSource is null
                ? await ValidateOutputAsync(item, partialPath, cancellationToken)
                : await ValidateSegmentedOutputAsync(item, partialPath, segmentedSource, cancellationToken);
            File.Move(partialPath, finalPath);
            stopwatch.Stop();

            var outputBytes = new FileInfo(finalPath).Length;
            RecordHistory(new HistoryRecord(
                DateTimeOffset.Now,
                settings.Engine,
                settings.ProfileCode,
                settings.Cq,
                item.InputBytes,
                outputBytes,
                item.Duration.TotalSeconds,
                stopwatch.Elapsed.TotalSeconds,
                item.InputWidth,
                item.InputHeight,
                item.InputBitRate,
                item.AudioTracks,
                item.HasAssSubtitle,
                item.InputVideoCodec,
                item.InputPixelFormat,
                settings.Concurrency,
                settings.IsBeta,
                EffectiveBetaSignature(settings),
                useSegmentedAnimeJanai ? SegmentedPipelineVersion : 0,
                useSegmentedAnimeJanai ? AnimeJanaiSegmentSeconds : 0));

            await Dispatcher.InvokeAsync(() =>
            {
                item.OutputPath = finalPath;
                item.ActualOutputBytes = outputBytes;
                item.Processed = item.Duration;
                item.Progress = 100;
                item.Status = "Concluído";
                item.Stage = "Finalizado";
                item.Compatibility = validation;
                item.IsRunning = false;
                item.UpdateProgress();
                UpdateEstimates();
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog(logPath, $"\n[{DateTime.Now:O}] Processamento cancelado; blocos concluídos preservados.\n");
            TryDelete(partialPath);
            await Dispatcher.InvokeAsync(() =>
            {
                item.Status = "Cancelado";
                item.Stage = "Cancelado";
                item.IsRunning = false;
            });
        }
        catch (Exception ex)
        {
            TryDelete(partialPath);
            AppendLog(logPath, $"\nERRO FINAL\n{ex}\n");
            await Dispatcher.InvokeAsync(() =>
            {
                item.Status = "Falhou";
                item.Error = FriendlyError(ex.Message);
                item.Compatibility = "Não validado";
                item.IsRunning = false;
            });
        }
        finally
        {
            if (!useSegmentedAnimeJanai)
                TryDeleteDirectory(workDirectory);
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateOutputDiskSpace(destinationDirectory);
                item.FinishTiming(DateTimeOffset.Now);
                RefreshOverall();
            });
        }
    }

    private async Task RunAnime4KAsync(EncodeItem item, JobSettings settings, string outputPath, string logPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ShaderPath)) throw new InvalidOperationException("O shader combinado não foi criado.");
        var args = BuildFfmpegArguments(item.InputPath, outputPath, settings.Cq, settings.ShaderPath);
        args = ApplyBetaOptions(args, settings.BetaOptions ?? new BetaFfmpegOptions());
        args = ApplyAdvancedFfmpegCommand(args, settings.AdvancedFfmpegCommand, item.InputPath, outputPath);

        await Dispatcher.InvokeAsync(() => { item.Status = "Processando"; item.Stage = "Anime4K + AV1"; });
        await RunFfmpegAsync(item, args, 0, 100, logPath, cancellationToken);
    }

    private async Task RunAnimeJanaiAsync(EncodeItem item, JobSettings settings, string outputPath, string workDirectory, string logPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelName)) throw new InvalidOperationException("Modelo AnimeJaNai não selecionado.");
        var modelPath = Path.Combine(_modelDirectory, settings.ModelName + ".onnx");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Modelo AnimeJaNai não encontrado.", modelPath);

        var configPath = Path.Combine(workDirectory, settings.ModelName + ".conf");
        var intermediatePath = Path.Combine(workDirectory, "animejanai-lossless-hevc.mkv");
        var config = $"""
            [global]
            config_version=2
            logging=yes
            backend=TensorRT

            [slot_1]
            profile_name=encode
            chain_1_min_resolution=0x0
            chain_1_max_resolution=0x0
            chain_1_min_fps=0
            chain_1_max_fps=0
            chain_1_model_1_resize_height_before_upscale=0
            chain_1_model_1_resize_factor_before_upscale=0
            chain_1_model_1_name={settings.ModelName}
            chain_1_rife=no
            chain_1_rife_model=
            chain_1_rife_factor_numerator=2
            chain_1_rife_factor_denominator=1
            chain_1_rife_scene_detect_threshold=0.2
            chain_1_rife_ensemble=no
            """;
        File.WriteAllText(configPath, config, new UTF8Encoding(false));

        var ajiArgs = new List<string>
        {
            "--input", item.InputPath,
            "--output", intermediatePath,
            "--conf", configPath,
            "--slot", "1",
            "--model-dir", _modelDirectory,
            "--trtexec", _trtexec,
            "--backend", "tensorrt",
            "--vcodec", "hevc_nvenc",
            "--vquality", "-preset p7 -tune lossless -rc constqp -qp 0",
            "--pix-fmt", "yuv420p10",
            "--overwrite",
            "--progress", "line"
        };

        await Dispatcher.InvokeAsync(() => { item.Status = "Processando"; item.Stage = "AnimeJaNai / TensorRT"; });
        await RunAjiAsync(item, ajiArgs, 0, 72, logPath, cancellationToken);
        if (!File.Exists(intermediatePath) || new FileInfo(intermediatePath).Length < 1024)
            throw new InvalidOperationException("O AnimeJaNai não produziu o arquivo intermediário esperado.");

        var ffmpegArgs = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-i", intermediatePath,
            "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?",
            "-map_metadata", "0", "-map_chapters", "0", "-c", "copy",
            "-c:v:0", "av1_nvenc", "-preset", "p7", "-tune", "uhq", "-rc", "vbr", "-b:v", "0",
            "-cq", settings.Cq.ToString(CultureInfo.InvariantCulture), "-multipass", "fullres",
            "-rc-lookahead", "32", "-lookahead_level", "3", "-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8",
            "-pix_fmt", "p010le", "-highbitdepth", "1",
            "-c:a", "aac", "-q:a", "2", "-ar:a", "48000", "-af:a", "aresample=async=1:first_pts=0",
            "-avoid_negative_ts", "make_zero", "-max_interleave_delta", "0", "-max_muxing_queue_size", "4096",
            "-progress", "pipe:1", "-nostats", outputPath
        };

        await Dispatcher.InvokeAsync(() => { item.Status = "Processando"; item.Stage = "Codificação AV1"; });
        await RunFfmpegAsync(item, ffmpegArgs, 72, 28, logPath, cancellationToken);
    }

    private async Task<MediaStructure> RunAnimeJanaiSegmentedAsync(
        EncodeItem item,
        JobSettings settings,
        string outputPath,
        string workDirectory,
        string logPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelName)) throw new InvalidOperationException("Modelo AnimeJaNai não selecionado.");
        var modelPath = Path.Combine(_modelDirectory, settings.ModelName + ".onnx");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Modelo AnimeJaNai não encontrado.", modelPath);

        await Dispatcher.InvokeAsync(() => { item.Status = "Verificando"; item.Stage = "Inventário e áudios da entrada"; });
        var source = await ProbeMediaStructureAsync(item.InputPath, true, cancellationToken);
        if (source.VideoPackets <= 0 || source.AverageFrameRate <= 0)
            throw new InvalidOperationException("Não foi possível determinar a quantidade de frames e o FPS da entrada para criar blocos exatos.");
        if (source.NominalFrameRate > 0 && Math.Abs(source.NominalFrameRate - source.AverageFrameRate) / source.AverageFrameRate > 0.001)
            throw new InvalidOperationException("A entrada parece usar frame rate variável. O modo segmentado exige vídeo CFR para garantir junções exatas.");

        await ValidateAudioStreamsAsync(item, item.InputPath, source.AudioStreams, "entrada", logPath, cancellationToken);

        Directory.CreateDirectory(workDirectory);
        var configPath = WriteAnimeJanaiConfig(workDirectory, settings.ModelName);
        await Dispatcher.InvokeAsync(() => { item.Status = "Preparando"; item.Stage = "Engine TensorRT e fingerprint"; });
        await PrepareTensorRtAsync(item, item.InputPath, configPath, 0, 0.5, logPath, cancellationToken);
        var toolchain = await ComputeToolchainSnapshotAsync(settings, cancellationToken);
        var cacheDirectory = Path.Combine(workDirectory, "toolchain-" + toolchain.Hash[..16].ToLowerInvariant());
        Directory.CreateDirectory(cacheDirectory);
        var framesPerSegment = Math.Max(1L, (long)Math.Round(source.AverageFrameRate * AnimeJanaiSegmentSeconds, MidpointRounding.AwayFromZero));
        var segments = BuildSegmentPlan(source.VideoPackets, framesPerSegment, AnimeJanaiOverlapFrames);
        var manifestPath = Path.Combine(cacheDirectory, "manifest.json");
        var manifest = LoadOrCreateSegmentManifest(manifestPath, item, settings, source, segments, toolchain.Hash);

        AppendLog(logPath,
            $"\n[{DateTime.Now:O}] AnimeJaNai segmentado recuperável\n" +
            $"Entrada={item.InputPath}\nFPS={source.AverageFrameRate:0.########}\nFrames={source.VideoPackets}\n" +
            $"Blocos={segments.Count}\nFramesPorBloco={framesPerSegment}\nSobreposição={AnimeJanaiOverlapFrames} frames por borda\n" +
            $"FingerprintToolchain={toolchain.Hash}\nComponentes={toolchain.Components.Count}\n" +
            $"Cache={cacheDirectory}\n");

        const double segmentProgressTotal = 94;
        var segmentProgressSpan = segmentProgressTotal / segments.Count;
        for (var index = 0; index < segments.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = segments[index];
            var checkpoint = manifest.Segments[index];
            var completedPath = Path.Combine(cacheDirectory, SegmentFileName(segment.Index));
            var partialSegmentPath = completedPath + ".partial.mkv";
            var sourceSegmentPath = Path.Combine(cacheDirectory, $"source-{segment.Index + 1:0000}.mkv");
            var sourcePartialPath = sourceSegmentPath + ".partial.mkv";
            var progressStart = index * segmentProgressSpan;

            if (File.Exists(completedPath))
            {
                try
                {
                    await ValidateVideoSegmentAsync(completedPath, segment.UsefulFrameCount, cancellationToken,
                        checked(source.Width * 2), checked(source.Height * 2));
                    checkpoint.Completed = true;
                    checkpoint.CompletedAt = File.GetLastWriteTimeUtc(completedPath);
                    SaveSegmentManifest(manifestPath, manifest);
                    AppendLog(logPath, $"\nBloco {index + 1}/{segments.Count} recuperado do cache e validado.\n");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        item.Stage = $"Bloco {index + 1}/{segments.Count} recuperado";
                        item.Progress = Math.Min(99.4, progressStart + segmentProgressSpan);
                        item.Processed = TimeSpan.FromSeconds(item.Duration.TotalSeconds * item.Progress / 100);
                        item.UpdateProgress();
                        RefreshOverall();
                    });
                    continue;
                }
                catch (Exception ex)
                {
                    AppendLog(logPath, $"\nBloco {index + 1} existente foi rejeitado e será refeito: {ex.Message}\n");
                    TryDelete(completedPath);
                    checkpoint.Completed = false;
                }
            }

            TryDelete(partialSegmentPath);
            TryDelete(sourcePartialPath);
            TryDelete(sourceSegmentPath);
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    item.Status = "Processando";
                    item.Stage = $"Preparando bloco {index + 1}/{segments.Count}";
                });

                var sourceDuration = TimeSpan.FromSeconds(segment.SourceFrameCount / source.AverageFrameRate);
                var sourceStartSeconds = segment.SourceStartFrame / source.AverageFrameRate;
                var sourceArgs = BuildLosslessSourceSegmentArguments(
                    item.InputPath,
                    sourcePartialPath,
                    sourceStartSeconds,
                    segment.SourceFrameCount,
                    source.PixelFormat);
                await RunFfmpegAsync(
                    item,
                    sourceArgs,
                    progressStart,
                    segmentProgressSpan * 0.12,
                    logPath,
                    cancellationToken,
                    sourceDuration,
                    $"Preparando bloco {index + 1}/{segments.Count}");

                var prepared = await ProbeMediaStructureAsync(sourcePartialPath, true, cancellationToken);
                if (prepared.VideoPackets != segment.SourceFrameCount)
                    throw new InvalidOperationException($"O trecho temporário {index + 1} contém {prepared.VideoPackets} frames; eram esperados {segment.SourceFrameCount}.");
                File.Move(sourcePartialPath, sourceSegmentPath, true);

                await RunAnimeJanaiSegmentPipeAsync(
                    item,
                    settings,
                    sourceSegmentPath,
                    partialSegmentPath,
                    configPath,
                    segment,
                    segments.Count,
                    progressStart + segmentProgressSpan * 0.12,
                    segmentProgressSpan * 0.88,
                    source.AverageFrameRate,
                    logPath,
                    cancellationToken);

                await ValidateVideoSegmentAsync(partialSegmentPath, segment.UsefulFrameCount, cancellationToken,
                    checked(source.Width * 2), checked(source.Height * 2));
                File.Move(partialSegmentPath, completedPath, true);
                checkpoint.Completed = true;
                checkpoint.CompletedAt = DateTimeOffset.Now;
                checkpoint.OutputBytes = new FileInfo(completedPath).Length;
                SaveSegmentManifest(manifestPath, manifest);
                AppendLog(logPath, $"\nBloco {index + 1}/{segments.Count} validado: {segment.UsefulFrameCount} frames, {FormatBytes(checkpoint.OutputBytes)}.\n");
            }
            finally
            {
                TryDelete(sourcePartialPath);
                TryDelete(sourceSegmentPath);
                TryDelete(partialSegmentPath);
            }
        }

        var concatPath = Path.Combine(cacheDirectory, "segments.ffconcat");
        WriteConcatManifest(concatPath, segments);
        var finalArgs = BuildSegmentedFinalMuxArguments(concatPath, item.InputPath, outputPath, source);
        await Dispatcher.InvokeAsync(() => { item.Status = "Finalizando"; item.Stage = "Remux das faixas originais"; });
        await RunFfmpegAsync(
            item,
            finalArgs,
            segmentProgressTotal,
            5.5,
            logPath,
            cancellationToken,
            TimeSpan.FromSeconds(source.VideoPackets / source.AverageFrameRate),
            "Remux das faixas originais");
        return source;
    }

    private async Task RunAnimeJanaiSegmentPipeAsync(
        EncodeItem item,
        JobSettings settings,
        string inputPath,
        string outputPath,
        string configPath,
        AnimeJanaiSegmentPlan segment,
        int segmentCount,
        double progressStart,
        double progressSpan,
        double frameRate,
        string logPath,
        CancellationToken cancellationToken)
    {
        var pipeName = "animejanai-segment-" + Guid.NewGuid().ToString("N") + ".mkv";
        var pipePath = @"\\.\pipe\" + pipeName;
        await using var pipeServer = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1024 * 1024, 1024 * 1024);

        var ajiArgs = new List<string>
        {
            "--input", inputPath,
            "--output", pipePath,
            "--conf", configPath,
            "--slot", "1",
            "--model-dir", _modelDirectory,
            "--trtexec", _trtexec,
            "--backend", "tensorrt",
            "--vcodec", "hevc_nvenc",
            "--vquality", "-preset p7 -tune lossless -rc constqp -qp 0",
            "--pix-fmt", "yuv420p10",
            "--no-audio", "--no-subs", "--no-chapters",
            "--overwrite",
            "--progress", "line"
        };

        var ffmpegArgs = BuildSegmentVideoArguments(
            "pipe:0",
            outputPath,
            settings.Cq,
            segment.PrefixOverlapFrames,
            segment.UsefulFrameCount);
        ffmpegArgs = ApplyBetaOptions(ffmpegArgs, settings.BetaOptions ?? new BetaFfmpegOptions());

        var ajiInfo = CreateProcessInfo(_ajiEncode, ajiArgs, _inferenceDirectory);
        var ffmpegInfo = CreateProcessInfo(_ffmpeg, ffmpegArgs, Path.GetDirectoryName(_ffmpeg)!);
        ffmpegInfo.RedirectStandardInput = true;
        AppendLog(logPath,
            $"\n[{DateTime.Now:O}] AnimeJaNai bloco {segment.Index + 1}/{segmentCount}\n{FormatCommand(_ajiEncode, ajiArgs)}\n" +
            $"\n[{DateTime.Now:O}] FFmpeg AV1 bloco {segment.Index + 1}/{segmentCount}\n{FormatCommand(_ffmpeg, ffmpegArgs)}\n");

        using var ajiProcess = new Process { StartInfo = ajiInfo };
        using var ffmpegProcess = new Process { StartInfo = ffmpegInfo };
        var connectionTask = pipeServer.WaitForConnectionAsync(cancellationToken);
        try
        {
            await Dispatcher.InvokeAsync(() =>
            {
                item.Status = "Processando";
                item.Stage = $"AnimeJaNai bloco {segment.Index + 1}/{segmentCount}";
            });
            ffmpegProcess.Start();
            ajiProcess.Start();

            using var registration = cancellationToken.Register(() =>
            {
                KillProcess(ajiProcess);
                KillProcess(ffmpegProcess);
            });

            var ajiOutputTask = ReadAnimeJanaiProgressAsync(ajiProcess, item, cancellationToken, $"AnimeJaNai bloco {segment.Index + 1}/{segmentCount}");
            var ajiErrorTask = ajiProcess.StandardError.ReadToEndAsync(cancellationToken);
            var ffmpegProgressTask = ReadFfmpegPipeProgressAsync(
                ffmpegProcess,
                item,
                TimeSpan.FromSeconds(segment.UsefulFrameCount / frameRate),
                progressStart,
                progressSpan,
                $"AnimeJaNai bloco {segment.Index + 1}/{segmentCount}",
                cancellationToken);
            var ffmpegErrorTask = ffmpegProcess.StandardError.ReadToEndAsync(cancellationToken);
            var ajiExitTask = ajiProcess.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(connectionTask, ajiExitTask);

            if (first == ajiExitTask && !pipeServer.IsConnected)
            {
                await ffmpegProcess.StandardInput.BaseStream.DisposeAsync();
                KillProcess(ffmpegProcess);
                var earlyError = await ajiErrorTask;
                await ajiOutputTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(earlyError)
                    ? $"AnimeJaNai terminou antes de conectar o bloco {segment.Index + 1} ao pipe."
                    : Tail(earlyError));
            }

            await connectionTask;
            try
            {
                await pipeServer.CopyToAsync(ffmpegProcess.StandardInput.BaseStream, 1024 * 1024, cancellationToken);
            }
            catch (IOException pipeError)
            {
                try { await ffmpegProcess.WaitForExitAsync(); } catch { }
                var earlyFfmpegError = await ffmpegErrorTask;
                var detail = string.IsNullOrWhiteSpace(earlyFfmpegError)
                    ? $"O FFmpeg encerrou o pipe do bloco {segment.Index + 1} antes de receber todos os frames."
                    : $"O FFmpeg encerrou o bloco {segment.Index + 1}: {Tail(earlyFfmpegError)}";
                throw new InvalidOperationException(detail, pipeError);
            }
            finally
            {
                await ffmpegProcess.StandardInput.BaseStream.DisposeAsync();
            }

            await ajiExitTask;
            await ffmpegProcess.WaitForExitAsync(cancellationToken);
            var ajiOutput = await ajiOutputTask;
            var ajiError = await ajiErrorTask;
            await ffmpegProgressTask;
            var ffmpegError = await ffmpegErrorTask;
            AppendLog(logPath,
                $"\nAnimeJaNai bloco {segment.Index + 1} stdout:\n{ajiOutput}\nAnimeJaNai stderr:\n{ajiError}\nAnimeJaNai ExitCode={ajiProcess.ExitCode}\n" +
                $"\nFFmpeg stderr:\n{ffmpegError}\nFFmpeg ExitCode={ffmpegProcess.ExitCode}\n");

            if (ajiProcess.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(ajiError) ? $"AnimeJaNai terminou o bloco {segment.Index + 1} com código {ajiProcess.ExitCode}." : Tail(ajiError));
            if (ffmpegProcess.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(ffmpegError) ? $"FFmpeg terminou o bloco {segment.Index + 1} com código {ffmpegProcess.ExitCode}." : Tail(ffmpegError));
        }
        catch
        {
            KillProcess(ajiProcess);
            KillProcess(ffmpegProcess);
            throw;
        }
    }

    private string WriteAnimeJanaiConfig(string workDirectory, string modelName)
    {
        var configPath = Path.Combine(workDirectory, modelName + ".conf");
        var config = $"""
            [global]
            config_version=2
            logging=yes
            backend=TensorRT

            [slot_1]
            profile_name=encode
            chain_1_min_resolution=0x0
            chain_1_max_resolution=0x0
            chain_1_min_fps=0
            chain_1_max_fps=0
            chain_1_model_1_resize_height_before_upscale=0
            chain_1_model_1_resize_factor_before_upscale=0
            chain_1_model_1_name={modelName}
            chain_1_rife=no
            chain_1_rife_model=
            chain_1_rife_factor_numerator=2
            chain_1_rife_factor_denominator=1
            chain_1_rife_scene_detect_threshold=0.2
            chain_1_rife_ensemble=no
            """;
        File.WriteAllText(configPath, config, new UTF8Encoding(false));
        return configPath;
    }

    private List<string> BuildAnimeJanaiEngineArguments(string inputPath, string configPath) =>
    [
        "--input", inputPath,
        "--conf", configPath,
        "--slot", "1",
        "--model-dir", _modelDirectory,
        "--trtexec", _trtexec,
        "--backend", "tensorrt",
        "--build-only",
        "--progress", "line"
    ];

    private async Task RunAnimeJanaiPipeAsync(EncodeItem item, JobSettings settings, string outputPath, string workDirectory, string logPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.ModelName)) throw new InvalidOperationException("Modelo AnimeJaNai não selecionado.");
        var modelPath = Path.Combine(_modelDirectory, settings.ModelName + ".onnx");
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Modelo AnimeJaNai não encontrado.", modelPath);

        var configPath = Path.Combine(workDirectory, settings.ModelName + ".conf");
        var config = $"""
            [global]
            config_version=2
            logging=yes
            backend=TensorRT

            [slot_1]
            profile_name=encode
            chain_1_min_resolution=0x0
            chain_1_max_resolution=0x0
            chain_1_min_fps=0
            chain_1_max_fps=0
            chain_1_model_1_resize_height_before_upscale=0
            chain_1_model_1_resize_factor_before_upscale=0
            chain_1_model_1_name={settings.ModelName}
            chain_1_rife=no
            chain_1_rife_model=
            chain_1_rife_factor_numerator=2
            chain_1_rife_factor_denominator=1
            chain_1_rife_scene_detect_threshold=0.2
            chain_1_rife_ensemble=no
            """;
        File.WriteAllText(configPath, config, new UTF8Encoding(false));

        await PrepareTensorRtAsync(item, item.InputPath, configPath, 0, 0.5, logPath, cancellationToken);
        var pipeName = "animejanai-" + Guid.NewGuid().ToString("N") + ".mkv";
        var pipePath = @"\\.\pipe\" + pipeName;
        await using var pipeServer = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            1024 * 1024, 1024 * 1024);

        var ajiArgs = new List<string>
        {
            "--input", item.InputPath,
            "--output", pipePath,
            "--conf", configPath,
            "--slot", "1",
            "--model-dir", _modelDirectory,
            "--trtexec", _trtexec,
            "--backend", "tensorrt",
            "--vcodec", "hevc_nvenc",
            "--vquality", "-preset p7 -tune lossless -rc constqp -qp 0",
            "--pix-fmt", "yuv420p10",
            "--overwrite",
            "--progress", "line"
        };

        var ffmpegArgs = BuildFfmpegArguments("pipe:0", outputPath, settings.Cq, null);
        ffmpegArgs = ApplyBetaOptions(ffmpegArgs, settings.BetaOptions ?? new BetaFfmpegOptions());
        ffmpegArgs = ApplyAdvancedFfmpegCommand(ffmpegArgs, settings.AdvancedFfmpegCommand, "pipe:0", outputPath);

        var ajiInfo = CreateProcessInfo(_ajiEncode, ajiArgs, _inferenceDirectory);
        var ffmpegInfo = CreateProcessInfo(_ffmpeg, ffmpegArgs, Path.GetDirectoryName(_ffmpeg)!);
        ffmpegInfo.RedirectStandardInput = true;
        AppendLog(logPath,
            $"\n[{DateTime.Now:O}] AnimeJaNai por pipe nomeado\n{FormatCommand(_ajiEncode, ajiArgs)}\n" +
            $"\n[{DateTime.Now:O}] FFmpeg AV1 conectado ao pipe\n{FormatCommand(_ffmpeg, ffmpegArgs)}\n");

        using var ajiProcess = new Process { StartInfo = ajiInfo };
        using var ffmpegProcess = new Process { StartInfo = ffmpegInfo };
        var connectionTask = pipeServer.WaitForConnectionAsync(cancellationToken);

        try
        {
            await Dispatcher.InvokeAsync(() => { item.Status = "Processando"; item.Stage = "AnimeJaNai + AV1 por pipe"; });
            ffmpegProcess.Start();
            ajiProcess.Start();

            using var registration = cancellationToken.Register(() =>
            {
                KillProcess(ajiProcess);
                KillProcess(ffmpegProcess);
            });

            var ajiOutputTask = ReadAnimeJanaiProgressAsync(ajiProcess, item, cancellationToken);
            var ajiErrorTask = ajiProcess.StandardError.ReadToEndAsync(cancellationToken);
            var ffmpegProgressTask = ReadFfmpegPipeProgressAsync(ffmpegProcess, item, cancellationToken);
            var ffmpegErrorTask = ffmpegProcess.StandardError.ReadToEndAsync(cancellationToken);
            var ajiExitTask = ajiProcess.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(connectionTask, ajiExitTask);

            if (first == ajiExitTask && !pipeServer.IsConnected)
            {
                await ffmpegProcess.StandardInput.BaseStream.DisposeAsync();
                KillProcess(ffmpegProcess);
                var earlyError = await ajiErrorTask;
                await ajiOutputTask;
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(earlyError)
                    ? "AnimeJaNai terminou antes de conectar ao pipe."
                    : Tail(earlyError));
            }

            await connectionTask;
            try
            {
                await pipeServer.CopyToAsync(ffmpegProcess.StandardInput.BaseStream, 1024 * 1024, cancellationToken);
            }
            catch (IOException pipeError)
            {
                try { await ffmpegProcess.WaitForExitAsync(); } catch { }
                var earlyFfmpegError = await ffmpegErrorTask;
                var detail = string.IsNullOrWhiteSpace(earlyFfmpegError)
                    ? "O FFmpeg encerrou o pipe antes de receber todos os frames."
                    : $"O FFmpeg encerrou o pipe: {Tail(earlyFfmpegError)}";
                throw new InvalidOperationException(detail, pipeError);
            }
            finally
            {
                await ffmpegProcess.StandardInput.BaseStream.DisposeAsync();
            }

            await ajiExitTask;
            await ffmpegProcess.WaitForExitAsync(cancellationToken);
            var ajiOutput = await ajiOutputTask;
            var ajiError = await ajiErrorTask;
            await ffmpegProgressTask;
            var ffmpegError = await ffmpegErrorTask;

            AppendLog(logPath,
                $"\nAnimeJaNai stdout:\n{ajiOutput}\nAnimeJaNai stderr:\n{ajiError}\nAnimeJaNai ExitCode={ajiProcess.ExitCode}\n" +
                $"\nFFmpeg stderr:\n{ffmpegError}\nFFmpeg ExitCode={ffmpegProcess.ExitCode}\n");

            if (ajiProcess.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(ajiError) ? $"AnimeJaNai terminou com código {ajiProcess.ExitCode}." : Tail(ajiError));
            if (ffmpegProcess.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(ffmpegError) ? $"FFmpeg terminou com código {ffmpegProcess.ExitCode}." : Tail(ffmpegError));
        }
        catch
        {
            KillProcess(ajiProcess);
            KillProcess(ffmpegProcess);
            throw;
        }
    }

    private async Task<string> ReadAnimeJanaiProgressAsync(Process process, EncodeItem item, CancellationToken cancellationToken, string? stageLabel = null)
    {
        var output = new StringBuilder();
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            output.AppendLine(line);
            var percent = ParseAnimeJanaiPercent(line);
            if (percent.HasValue)
                await Dispatcher.InvokeAsync(() =>
                {
                    item.Stage = stageLabel is null
                        ? $"AnimeJaNai {percent.Value:0}% + AV1"
                        : $"{stageLabel} • {percent.Value:0}%";
                    item.UpdateProgress();
                });
        }
        return output.ToString();
    }

    private async Task ReadFfmpegPipeProgressAsync(Process process, EncodeItem item, CancellationToken cancellationToken)
    {
        await ReadFfmpegPipeProgressAsync(process, item, item.Duration, 0, 100, "AnimeJaNai + AV1 por pipe", cancellationToken);
    }

    private async Task ReadFfmpegPipeProgressAsync(
        Process process,
        EncodeItem item,
        TimeSpan progressDuration,
        double progressStart,
        double progressSpan,
        string stageLabel,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            var key = line[..split];
            var value = line[(split + 1)..];
            if (key is "out_time_us" or "out_time_ms" && long.TryParse(value, out var us))
            {
                var processed = TimeSpan.FromTicks(us * 10);
                await Dispatcher.InvokeAsync(() => ApplyStageProgress(item, processed, progressStart, progressSpan, progressDuration, stageLabel));
            }
            else if (key == "speed")
            {
                await Dispatcher.InvokeAsync(() => { item.Speed = value; item.UpdateProgress(); RefreshOverall(); });
            }
        }
    }

    private async Task RunFfmpegAsync(
        EncodeItem item,
        IReadOnlyList<string> arguments,
        double progressStart,
        double progressSpan,
        string logPath,
        CancellationToken cancellationToken,
        TimeSpan? progressDuration = null,
        string? stageLabel = null)
    {
        var psi = CreateProcessInfo(_ffmpeg, arguments, Path.GetDirectoryName(_ffmpeg)!);
        AppendLog(logPath, $"\n[{DateTime.Now:O}] FFmpeg\n{FormatCommand(_ffmpeg, arguments)}\n");
        using var process = new Process { StartInfo = psi };
        var errors = new StringBuilder();
        var errorLock = new object();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errorLock)
            {
                errors.AppendLine(e.Data);
                if (errors.Length > 50000) errors.Remove(0, 25000);
            }
        };

        process.Start();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() => KillProcess(process));
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            var split = line.IndexOf('=');
            if (split <= 0) continue;
            var key = line[..split];
            var value = line[(split + 1)..];
            if (key is "out_time_us" or "out_time_ms" && long.TryParse(value, out var us))
            {
                var processed = TimeSpan.FromTicks(us * 10);
                await Dispatcher.InvokeAsync(() => ApplyStageProgress(
                    item,
                    processed,
                    progressStart,
                    progressSpan,
                    progressDuration ?? item.Duration,
                    stageLabel));
            }
            else if (key == "speed")
            {
                await Dispatcher.InvokeAsync(() => { item.Speed = value; item.UpdateProgress(); RefreshOverall(); });
            }
        }

        await process.WaitForExitAsync(cancellationToken);
        string errorText;
        lock (errorLock) errorText = errors.ToString();
        AppendLog(logPath, errorText + $"\nExitCode={process.ExitCode}\n");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(errorText) ? $"FFmpeg terminou com código {process.ExitCode}." : Tail(errorText));
    }

    private async Task RunAjiAsync(EncodeItem item, IReadOnlyList<string> arguments, double progressStart, double progressSpan, string logPath, CancellationToken cancellationToken)
    {
        var psi = CreateProcessInfo(_ajiEncode, arguments, _inferenceDirectory);
        AppendLog(logPath, $"\n[{DateTime.Now:O}] AnimeJaNai\n{FormatCommand(_ajiEncode, arguments)}\n");
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        var outputLock = new object();

        void HandleLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (outputLock)
            {
                output.AppendLine(line);
                if (output.Length > 60000) output.Remove(0, 30000);
            }
            var percent = ParseAnimeJanaiPercent(line);
            if (percent.HasValue)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    item.Progress = Math.Clamp(progressStart + percent.Value / 100 * progressSpan, 0, 99.8);
                    item.Processed = TimeSpan.FromSeconds(item.Duration.TotalSeconds * item.Progress / 100);
                    item.UpdateProgress();
                    RefreshOverall();
                });
            }
            else
            {
                var time = ParseProgressTime(line);
                if (time.HasValue)
                    Dispatcher.BeginInvoke(() => ApplyStageProgress(item, time.Value, progressStart, progressSpan));
            }
        }

        process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() => KillProcess(process));
        await process.WaitForExitAsync(cancellationToken);

        string outputText;
        lock (outputLock) outputText = output.ToString();
        AppendLog(logPath, outputText + $"\nExitCode={process.ExitCode}\n");
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(outputText) ? $"AnimeJaNai terminou com código {process.ExitCode}." : Tail(outputText));
    }

    private static ProcessStartInfo CreateProcessInfo(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return psi;
    }

    private void ApplyStageProgress(EncodeItem item, TimeSpan stageTime, double progressStart, double progressSpan)
        => ApplyStageProgress(item, stageTime, progressStart, progressSpan, item.Duration, null);

    private void ApplyStageProgress(
        EncodeItem item,
        TimeSpan stageTime,
        double progressStart,
        double progressSpan,
        TimeSpan progressDuration,
        string? stageLabel)
    {
        if (progressDuration.TotalSeconds <= 0) return;
        var stageRatio = Math.Clamp(stageTime.TotalSeconds / progressDuration.TotalSeconds, 0, 1);
        item.Progress = Math.Clamp(progressStart + stageRatio * progressSpan, 0, 99.8);
        item.Processed = TimeSpan.FromSeconds(item.Duration.TotalSeconds * item.Progress / 100);
        if (!string.IsNullOrWhiteSpace(stageLabel)) item.Stage = stageLabel;
        item.UpdateProgress();
        RefreshOverall();
    }

    private async Task<string> ValidateOutputAsync(EncodeItem sourceItem, string path, CancellationToken cancellationToken,
        int expectedWidth = 3840, int expectedHeight = 2160)
    {
        var media = await ProbeMediaAsync(path, cancellationToken);
        if (media.Duration.TotalSeconds < 1) throw new InvalidOperationException("A saída não possui duração válida.");
        var tolerance = Math.Max(3, sourceItem.Duration.TotalSeconds * 0.01);
        if (Math.Abs(media.Duration.TotalSeconds - sourceItem.Duration.TotalSeconds) > tolerance)
            throw new InvalidOperationException($"A duração da saída difere da entrada: {media.Duration:hh\\:mm\\:ss} vs. {sourceItem.Duration:hh\\:mm\\:ss}.");
        if (!string.Equals(media.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Codec de vídeo inesperado: {media.VideoCodec}.");
        if (media.Width != expectedWidth || media.Height != expectedHeight)
            throw new InvalidOperationException($"Resolução inesperada: {media.Width}×{media.Height}; esperado {expectedWidth}×{expectedHeight}.");
        if (!media.PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) && !media.PixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A saída não foi identificada como 10-bit: {media.PixelFormat}.");

        if (media.BitRate > 100_000_000) return "Bitrate acima do perfil Fire TV";
        return media.HasAssSubtitle
            ? "Vídeo direto; ASS pode transcodificar"
            : "Provável reprodução direta";
    }

    private async Task<string> ValidateSegmentedOutputAsync(
        EncodeItem sourceItem,
        string path,
        MediaStructure source,
        CancellationToken cancellationToken)
    {
        var compatibility = await ValidateOutputAsync(sourceItem, path, cancellationToken,
            checked(source.Width * 2), checked(source.Height * 2));
        var output = await ProbeMediaStructureAsync(path, true, cancellationToken);
        if (output.VideoPackets != source.VideoPackets)
            throw new InvalidOperationException($"A saída segmentada contém {output.VideoPackets} frames; a entrada contém {source.VideoPackets}.");
        if (output.AudioStreams != source.AudioStreams)
            throw new InvalidOperationException($"Quantidade de áudios alterada: {output.AudioStreams} na saída vs. {source.AudioStreams} na entrada.");
        if (output.SubtitleStreams != source.SubtitleStreams)
            throw new InvalidOperationException($"Quantidade de legendas alterada: {output.SubtitleStreams} na saída vs. {source.SubtitleStreams} na entrada.");
        if (output.AttachmentStreams != source.AttachmentStreams)
            throw new InvalidOperationException($"Quantidade de anexos alterada: {output.AttachmentStreams} na saída vs. {source.AttachmentStreams} na entrada.");
        if (output.Chapters != source.Chapters)
            throw new InvalidOperationException($"Quantidade de capítulos alterada: {output.Chapters} na saída vs. {source.Chapters} na entrada.");
        if (output.VideoIdentity != source.VideoIdentity)
            throw new InvalidOperationException(DescribeStreamIdentityMismatch("vídeo", 0, source.VideoIdentity, output.VideoIdentity));
        foreach (var type in new[] { "audio", "subtitle", "attachment" })
        {
            var expectedStreams = source.PreservedStreams.Where(stream => stream.Type == type).ToArray();
            var actualStreams = output.PreservedStreams.Where(stream => stream.Type == type).ToArray();
            for (var index = 0; index < expectedStreams.Length; index++)
            {
                if (actualStreams[index] != expectedStreams[index])
                    throw new InvalidOperationException(DescribeStreamIdentityMismatch(type, index, expectedStreams[index], actualStreams[index]));
            }
        }
        if (!output.ChapterEntries.Zip(source.ChapterEntries).All(pair =>
                Math.Abs(pair.First.StartSeconds - pair.Second.StartSeconds) <= 0.001 &&
                Math.Abs(pair.First.EndSeconds - pair.Second.EndSeconds) <= 0.001 &&
                pair.First.Title == pair.Second.Title))
            throw new InvalidOperationException("Os tempos ou títulos dos capítulos diferem do arquivo original.");

        var timeline = await ProbeVideoTimelineAsync(path, cancellationToken);
        var frameDuration = 1.0 / source.AverageFrameRate;
        var expectedLastPts = (source.VideoPackets - 1) * frameDuration;
        if (timeline.PacketCount != source.VideoPackets)
            throw new InvalidOperationException($"A linha temporal contém {timeline.PacketCount} pacotes; eram esperados {source.VideoPackets}.");
        if (Math.Abs(timeline.FirstPts) > frameDuration)
            throw new InvalidOperationException($"O vídeo final não começa próximo de zero: PTS inicial {timeline.FirstPts:0.######} s.");
        if (timeline.MinimumStep <= 0)
            throw new InvalidOperationException("A linha temporal final contém timestamps duplicados ou fora de ordem.");
        if (timeline.MaximumStep > frameDuration * 1.55 + 0.002)
            throw new InvalidOperationException($"Foi detectada uma lacuna de {timeline.MaximumStep:0.######} s entre frames na saída.");
        if (Math.Abs(timeline.LastPts - expectedLastPts) > frameDuration + 0.002)
            throw new InvalidOperationException($"O último frame está em {timeline.LastPts:0.######} s; era esperado aproximadamente {expectedLastPts:0.######} s.");

        await ValidateAudioStreamsAsync(sourceItem, path, output.AudioStreams, "saída", sourceItem.LogPath, cancellationToken);
        AppendLog(sourceItem.LogPath,
            $"\nValidação segmentada concluída: {output.VideoPackets} frames, {output.AudioStreams} áudios, " +
            $"{output.SubtitleStreams} legendas, {output.AttachmentStreams} anexos e {output.Chapters} capítulos. " +
            $"PTS {timeline.FirstPts:0.######}..{timeline.LastPts:0.######} s; maior passo {timeline.MaximumStep:0.######} s.\n");
        return compatibility;
    }

    private async Task ValidateVideoSegmentAsync(string path, long expectedFrames, CancellationToken cancellationToken,
        int expectedWidth = 3840, int expectedHeight = 2160)
    {
        var media = await ProbeMediaStructureAsync(path, true, cancellationToken);
        if (!string.Equals(media.VideoCodec, "av1", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bloco com codec inesperado: {media.VideoCodec}.");
        if (media.Width != expectedWidth || media.Height != expectedHeight)
            throw new InvalidOperationException($"Bloco com resolução inesperada: {media.Width}×{media.Height}; esperado {expectedWidth}×{expectedHeight}.");
        if (!media.PixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) &&
            !media.PixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bloco não identificado como 10-bit: {media.PixelFormat}.");
        if (media.VideoPackets != expectedFrames)
            throw new InvalidOperationException($"Bloco contém {media.VideoPackets} frames; eram esperados {expectedFrames}.");
        if (media.AudioStreams != 0 || media.SubtitleStreams != 0 || media.AttachmentStreams != 0)
            throw new InvalidOperationException("Um bloco intermediário contém faixas que deveriam permanecer somente no arquivo original.");
    }

    private async Task ValidateAudioStreamsAsync(
        EncodeItem item,
        string path,
        int audioStreams,
        string label,
        string logPath,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < audioStreams; index++)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                item.Stage = $"Validando áudio {index + 1}/{audioStreams} da {label}";
            });
            var args = new[]
            {
                "-hide_banner", "-nostdin", "-v", "error", "-i", path,
                "-map", $"0:a:{index}", "-f", "null", "NUL"
            };
            var psi = CreateProcessInfo(_ffmpeg, args, Path.GetDirectoryName(_ffmpeg)!);
            using var process = new Process { StartInfo = psi };
            process.Start();
            using var registration = cancellationToken.Register(() => KillProcess(process));
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            AppendLog(logPath,
                $"\n[{DateTime.Now:O}] Validação do áudio {index + 1}/{audioStreams} da {label}\n" +
                $"{FormatCommand(_ffmpeg, args)}\n{output}{error}\nExitCode={process.ExitCode}\n");
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"O áudio {index + 1} da {label} falhou na validação."
                    : $"O áudio {index + 1} da {label} é inválido: {Tail(error)}");
        }
    }

    private async Task<MediaInfo> ProbeMediaAsync(string path, CancellationToken cancellationToken = default)
    {
        var args = new[]
        {
            "-v", "error", "-show_entries",
            "format=duration,size,bit_rate:stream=codec_type,codec_name,width,height,pix_fmt,bit_rate,channels",
            "-of", "json", path
        };
        var psi = CreateProcessInfo(_ffprobe, args, Path.GetDirectoryName(_ffprobe)!);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o FFprobe.");
        var jsonTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await jsonTask;
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFprobe falhou." : Tail(error));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var duration = ParseJsonDouble(format, "duration");
        var size = ParseJsonLong(format, "size");
        var bitRate = ParseJsonLong(format, "bit_rate");
        string videoCodec = "";
        string pixelFormat = "";
        var width = 0;
        var height = 0;
        var hasAss = false;
        var audioCodecs = new List<string>();
        long estimatedAudioOutputBitRate = 0;

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetJsonString(stream, "codec_type");
                var codec = GetJsonString(stream, "codec_name");
                if (type == "video" && string.IsNullOrEmpty(videoCodec))
                {
                    videoCodec = codec;
                    pixelFormat = GetJsonString(stream, "pix_fmt");
                    width = ParseJsonInt(stream, "width");
                    height = ParseJsonInt(stream, "height");
                }
                else if (type == "audio")
                {
                    audioCodecs.Add(codec);
                    var channels = Math.Max(1, ParseJsonInt(stream, "channels"));
                    estimatedAudioOutputBitRate += channels switch
                    {
                        <= 2 => 192_000,
                        <= 6 => 384_000,
                        _ => 512_000
                    };
                }
                else if (type == "subtitle" && codec is "ass" or "ssa")
                {
                    hasAss = true;
                }
            }
        }

        return new MediaInfo(TimeSpan.FromSeconds(duration), size, bitRate, videoCodec, pixelFormat, width, height, hasAss, audioCodecs, estimatedAudioOutputBitRate);
    }

    private async Task<long> ProbeVideoPacketBytesAsync(string path, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=size", "-of", "csv=p=0", path
        };
        var psi = CreateProcessInfo(_ffprobe, args, Path.GetDirectoryName(_ffprobe)!);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o FFprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFprobe falhou ao medir o bitrate do piloto." : Tail(error));

        return output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0)
            .Sum();
    }

    private async Task<MediaStructure> ProbeMediaStructureAsync(string path, bool countPackets, CancellationToken cancellationToken)
    {
        var args = new List<string> { "-v", "error" };
        if (countPackets) args.Add("-count_packets");
        args.AddRange([
            "-show_entries",
            "format=duration:stream=codec_type,codec_name,width,height,pix_fmt,avg_frame_rate,r_frame_rate,nb_read_packets:" +
            "stream_tags=language,title,filename,mimetype:stream_disposition:" +
            "chapter=start_time,end_time:chapter_tags=title",
            "-of", "json", path]);
        var psi = CreateProcessInfo(_ffprobe, args, Path.GetDirectoryName(_ffprobe)!);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o FFprobe.");
        var jsonTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var json = await jsonTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFprobe falhou ao inventariar a mídia." : Tail(error));

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var duration = root.TryGetProperty("format", out var format) ? ParseJsonDouble(format, "duration") : 0;
        var videoCodec = "";
        var pixelFormat = "";
        var width = 0;
        var height = 0;
        var averageFrameRate = 0d;
        var nominalFrameRate = 0d;
        long videoPackets = 0;
        var audioStreams = 0;
        var subtitleStreams = 0;
        var attachmentStreams = 0;
        var videoIdentity = StreamIdentity.Empty("video");
        var preservedStreams = new List<StreamIdentity>();

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = GetJsonString(stream, "codec_type");
                if (type == "video" && string.IsNullOrWhiteSpace(videoCodec))
                {
                    videoCodec = GetJsonString(stream, "codec_name");
                    pixelFormat = GetJsonString(stream, "pix_fmt");
                    width = ParseJsonInt(stream, "width");
                    height = ParseJsonInt(stream, "height");
                    averageFrameRate = ParseRational(GetJsonString(stream, "avg_frame_rate"));
                    nominalFrameRate = ParseRational(GetJsonString(stream, "r_frame_rate"));
                    videoPackets = ParseJsonLongFlexible(stream, "nb_read_packets");
                    videoIdentity = BuildStreamIdentity(stream, type);
                }
                else if (type is "audio" or "subtitle" or "attachment")
                {
                    if (type == "audio") audioStreams++;
                    else if (type == "subtitle") subtitleStreams++;
                    else attachmentStreams++;
                    preservedStreams.Add(BuildStreamIdentity(stream, type));
                }
            }
        }

        if (videoPackets <= 0 && duration > 0 && averageFrameRate > 0)
            videoPackets = (long)Math.Round(duration * averageFrameRate, MidpointRounding.AwayFromZero);
        var chapters = new List<ChapterIdentity>();
        if (root.TryGetProperty("chapters", out var chapterArray))
        {
            foreach (var chapter in chapterArray.EnumerateArray())
                chapters.Add(new ChapterIdentity(
                    ParseJsonDouble(chapter, "start_time"),
                    ParseJsonDouble(chapter, "end_time"),
                    GetNestedJsonString(chapter, "tags", "title")));
        }
        return new MediaStructure(
            duration,
            videoCodec,
            pixelFormat,
            width,
            height,
            averageFrameRate,
            nominalFrameRate,
            videoPackets,
            audioStreams,
            subtitleStreams,
            attachmentStreams,
            videoIdentity,
            preservedStreams,
            chapters);
    }

    private async Task<VideoTimeline> ProbeVideoTimelineAsync(string path, CancellationToken cancellationToken)
    {
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-show_entries", "packet=pts_time", "-of", "csv=p=0", path
        };
        var psi = CreateProcessInfo(_ffprobe, args, Path.GetDirectoryName(_ffprobe)!);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o FFprobe.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "FFprobe falhou ao verificar timestamps." : Tail(error));

        var timestamps = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(value => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN)
            .Where(value => !double.IsNaN(value))
            .Order()
            .ToArray();
        if (timestamps.Length == 0) throw new InvalidOperationException("Nenhum timestamp de vídeo foi encontrado na saída.");

        var minimumStep = double.MaxValue;
        var maximumStep = 0d;
        for (var index = 1; index < timestamps.Length; index++)
        {
            var step = timestamps[index] - timestamps[index - 1];
            minimumStep = Math.Min(minimumStep, step);
            maximumStep = Math.Max(maximumStep, step);
        }
        if (timestamps.Length == 1) minimumStep = maximumStep = 0;
        return new VideoTimeline(timestamps.Length, timestamps[0], timestamps[^1], minimumStep, maximumStep);
    }

    private void UpdateEstimates()
    {
        if (!IsInitialized || EngineCombo.SelectedItem is not EngineOption engine || ProfileCombo.SelectedItem is not IProcessingProfile profile) return;
        var cq = int.TryParse(CqText.Text, out var parsed) ? Math.Clamp(parsed, 0, 63) : 28;
        var isBeta = BetaModeCheck.IsChecked == true;
        var betaSignature = isBeta ? ReadBetaOptions().Signature : "";
        long total = 0;
        double totalSeconds = 0;
        var sampleCount = 0;
        double confidenceTotal = 0;
        var pilotApplied = false;
        var concurrency = engine.Code == "animejanai" ? 1 : ConcurrencyCombo.SelectedIndex + 1;
        var pipelineVersion = engine.Code == "animejanai" && AdvancedModeCheck.IsChecked != true
            ? SegmentedPipelineVersion
            : 0;

        foreach (var item in Items)
        {
            var estimate = BuildAdaptiveEstimate(item, engine.Code, profile.Code, cq, concurrency, isBeta, betaSignature, pipelineVersion);
            var matchingPilot = _lastPilotResult is { } candidate &&
                                candidate.Signature == BuildPilotSignature(item, engine.Code, profile.Code, cq, isBeta, betaSignature) &&
                                !string.IsNullOrWhiteSpace(_activeToolchainFingerprint) &&
                                candidate.ToolchainFingerprint == _activeToolchainFingerprint
                ? candidate
                : null;
            if (matchingPilot is not null)
            {
                item.EstimatedOutputBytes = matchingPilot.ProjectedOutputBytes;
                item.EstimatedTotalSeconds = matchingPilot.ProjectedSeconds;
                item.EstimateBasis = $"piloto medido em {matchingPilot.SampleCount} trechos ({matchingPilot.SampleSeconds:0.#} s), margem ±15%, toolchain {matchingPilot.ToolchainFingerprint[..12]}";
                sampleCount += matchingPilot.SampleCount;
                confidenceTotal += 0.8;
                pilotApplied = true;
            }
            else
            {
                item.EstimatedOutputBytes = estimate.OutputBytes;
                item.EstimatedTotalSeconds = estimate.TotalSeconds;
                item.EstimateBasis = estimate.Basis;
                sampleCount += estimate.SampleCount;
                confidenceTotal += estimate.Confidence;
            }
            total += item.EstimatedOutputBytes;
            totalSeconds += item.EstimatedTotalSeconds;
        }

        var averageConfidence = Items.Count > 0 ? confidenceTotal / Items.Count : 0;
        var confidenceText = averageConfidence >= 0.75 ? "alta" : averageConfidence >= 0.35 ? "em aprendizado" : "inicial";
        var modelText = pilotApplied
            ? $"piloto medido aplicado; demais itens usam histórico {(isBeta ? "beta" : "estável")}; confiança {confidenceText}"
            : sampleCount > 0 ? $"{(isBeta ? "beta" : "estável")}, {sampleCount} amostras; confiança {confidenceText}" : $"{(isBeta ? "beta" : "estável")}, aguardando primeiras execuções";
        var queueSeconds = totalSeconds / Math.Max(1, concurrency);
        EstimateSummaryText.Text = Items.Count == 0
            ? "Estimativa: aguardando arquivos"
            : $"Saída aprox.: {FormatBytes(total)} • Tempo da fila: {FormatDuration(TimeSpan.FromSeconds(queueSeconds))} • Modelo: {modelText}";
        if (_lastPilotResult is { } visiblePilot && Items.Any(item =>
                visiblePilot.Signature == BuildPilotSignature(item, engine.Code, profile.Code, cq, isBeta, betaSignature)) &&
            !string.IsNullOrWhiteSpace(_activeToolchainFingerprint) &&
            visiblePilot.ToolchainFingerprint == _activeToolchainFingerprint)
            ShowPilotResult(visiblePilot);
        else if (_activePilotItem is null)
            PilotSummaryPanel.Visibility = Visibility.Collapsed;
        RefreshOverall();
    }

    private AdaptiveEstimate BuildAdaptiveEstimate(EncodeItem item, string engine, string profile, int cq, int concurrency, bool isBeta, string betaSignature, int pipelineVersion)
    {
        List<HistoryRecord> history;
        lock (_historyLock) history = _history.ToList();

        var candidates = history
            .Where(h => h.Engine == engine && h.IsBeta == isBeta && h.BetaSignature == betaSignature &&
                        (engine != "animejanai" || h.PipelineVersion == pipelineVersion) &&
                        h.DurationSeconds > 0 && h.ElapsedSeconds > 0 && h.OutputBytes > 0)
            .Select(h => new { Record = h, Distance = EstimateDistance(h, item, profile, cq, concurrency) })
            .OrderBy(x => x.Distance)
            .Take(80)
            .ToArray();

        var weighted = candidates
            .Select(x =>
            {
                var ageDays = Math.Max(0, (DateTimeOffset.Now - x.Record.Timestamp).TotalDays);
                var recency = 1.0 / (1.0 + ageDays / 90.0);
                var weight = Math.Exp(-x.Distance) * recency;
                return (x.Record, Weight: weight);
            })
            .Where(x => x.Weight > 0.02)
            .ToArray();

        var weightTotal = weighted.Sum(x => x.Weight);
        var learnedFactor = Math.Clamp(weightTotal / 5.0, 0, 0.88);
        var defaultRatio = DefaultSizeRatio(engine) * Math.Pow(2, (28 - cq) / 6.0);
        var defaultOutput = Math.Max(0, item.InputBytes * Math.Clamp(defaultRatio, 0.35, 12));
        var defaultTimeFactor = DefaultRealTimeFactor(engine, profile);
        var defaultTime = Math.Max(1, item.Duration.TotalSeconds * defaultTimeFactor);

        if (weightTotal <= 0 || item.Duration.TotalSeconds <= 0)
            return new AdaptiveEstimate((long)defaultOutput, defaultTime, 0, 0, "estimativa padrão");

        var learnedOutputRate = weighted.Sum(x => x.Record.OutputBytes / x.Record.DurationSeconds * x.Weight) / weightTotal;
        var learnedTimeFactor = weighted.Sum(x => x.Record.ElapsedSeconds / x.Record.DurationSeconds * x.Weight) / weightTotal;
        var learnedOutput = Math.Max(0, learnedOutputRate * item.Duration.TotalSeconds);
        var learnedTime = Math.Max(1, learnedTimeFactor * item.Duration.TotalSeconds);

        var output = (long)Math.Clamp(defaultOutput * (1 - learnedFactor) + learnedOutput * learnedFactor, 1, long.MaxValue);
        var time = Math.Max(1, defaultTime * (1 - learnedFactor) + learnedTime * learnedFactor);
        var confidence = Math.Clamp(learnedFactor * Math.Min(1, weighted.Length / 5.0), 0, 1);
        var exactSamples = weighted.Count(x => x.Record.Profile == profile && Math.Abs(x.Record.Cq - cq) <= 3);
        var basis = exactSamples > 0 ? $"{exactSamples} execuções semelhantes" : $"{weighted.Length} execuções do motor";
        return new AdaptiveEstimate(output, time, weighted.Length, confidence, basis);
    }

    private static double EstimateDistance(HistoryRecord record, EncodeItem item, string profile, int cq, int concurrency)
    {
        var distance = record.Profile == profile ? 0 : 1.35;
        distance += Math.Abs(record.Cq - cq) / 6.0;
        if (record.Concurrency > 0) distance += Math.Abs(record.Concurrency - concurrency) * 0.35;

        var itemPixels = Math.Max(1, item.InputWidth * item.InputHeight);
        var recordPixels = Math.Max(1, record.InputWidth * record.InputHeight);
        distance += Math.Abs(Math.Log((double)itemPixels / recordPixels, 2)) * 0.55;

        var itemSourceRate = item.InputBitRate > 0
            ? item.InputBitRate
            : item.Duration.TotalSeconds > 0 ? item.InputBytes * 8.0 / item.Duration.TotalSeconds : 0;
        var recordSourceRate = record.InputBitRate > 0
            ? record.InputBitRate
            : record.DurationSeconds > 0 ? record.InputBytes * 8.0 / record.DurationSeconds : 0;
        if (itemSourceRate > 0 && recordSourceRate > 0)
            distance += Math.Abs(Math.Log(itemSourceRate / recordSourceRate, 2)) * 0.45;

        if (item.AudioTracks > 0 && record.AudioTracks > 0)
            distance += Math.Abs(item.AudioTracks - record.AudioTracks) * 0.16;
        if (item.HasAssSubtitle != record.HasAssSubtitle) distance += 0.18;
        if (!string.IsNullOrWhiteSpace(item.InputVideoCodec) && !string.IsNullOrWhiteSpace(record.InputVideoCodec) &&
            !string.Equals(item.InputVideoCodec, record.InputVideoCodec, StringComparison.OrdinalIgnoreCase)) distance += 0.16;
        if (!string.IsNullOrWhiteSpace(item.InputPixelFormat) && !string.IsNullOrWhiteSpace(record.InputPixelFormat))
        {
            var itemIs10Bit = item.InputPixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) || item.InputPixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
            var recordIs10Bit = record.InputPixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) || record.InputPixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
            if (itemIs10Bit != recordIs10Bit) distance += 0.12;
        }
        return distance;
    }

    private static double DefaultSizeRatio(string engine) => engine == "animejanai" ? 2.2 : 1.8;
    private static double DefaultRealTimeFactor(string engine, string profile) => engine == "anime4k"
        ? 0.75
        : profile.Contains("Performance", StringComparison.OrdinalIgnoreCase) ? 2.5 : 4.5;

    private void RefreshOverall()
    {
        if (_activePilotItem is { } pilot)
        {
            OverallProgress.Value = pilot.Progress;
            OverallPercentText.Text = $"{pilot.Progress:0.0}%";
            OverallStatusText.Text = $"Piloto em execução • {pilot.ProgressText}";
            return;
        }

        var total = Items.Sum(i => i.Duration.TotalSeconds);
        var done = Items.Sum(i => Math.Min(i.Processed.TotalSeconds, i.Duration.TotalSeconds));
        var percent = total > 0 ? done / total * 100 : 0;
        OverallProgress.Value = percent;
        OverallPercentText.Text = $"{percent:0.0}%";

        var completed = Items.Count(i => i.Status == "Concluído");
        var running = Items.Count(i => i.IsRunning);
        var remainingSeconds = Items.Where(i => i.Status != "Concluído").Sum(i => i.RemainingEstimateSeconds);
        var concurrency = EngineCombo.SelectedItem is EngineOption { Code: "anime4k" } ? ConcurrencyCombo.SelectedIndex + 1 : 1;
        var eta = TimeSpan.FromSeconds(Math.Max(0, remainingSeconds / Math.Max(1, concurrency)));
        var etaText = remainingSeconds > 0 ? FormatDuration(eta) : "—";
        var elapsedText = "";
        if (_queueStartedAt.HasValue)
        {
            var elapsed = _queueElapsed ?? (DateTimeOffset.Now - _queueStartedAt.Value);
            elapsedText = running > 0 ? $" • Decorrido: {FormatDuration(elapsed)}" : $" • Tempo total: {FormatDuration(elapsed)}";
        }
        OverallStatusText.Text = $"{completed}/{Items.Count} concluídos • {running} processando • ETA total: {etaText}{elapsedText}";
    }

    private void SetRunningUi(bool running)
    {
        if (running) _pilotCacheRefreshCancellation?.Cancel();
        StartButton.IsEnabled = !running && _environmentReady;
        PilotButton.IsEnabled = !running && _environmentReady;
        CancelButton.IsEnabled = running;
        EngineCombo.IsEnabled = !running;
        ProfileCombo.IsEnabled = !running;
        CqText.IsEnabled = !running;
        OutputDirectoryText.IsEnabled = !running;
        ConcurrencyCombo.IsEnabled = !running && EngineCombo.SelectedItem is EngineOption { Code: "anime4k" };
        AdvancedModeCheck.IsEnabled = !running;
        BetaModeCheck.IsEnabled = !running;
        BetaPanel.IsEnabled = !running;
    }

    private void LoadFileHashCache()
    {
        try
        {
            if (!File.Exists(_fileHashCachePath)) return;
            var entries = JsonSerializer.Deserialize<List<FileHashCacheEntry>>(File.ReadAllText(_fileHashCachePath));
            if (entries is null) return;
            foreach (var entry in entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Path) && !string.IsNullOrWhiteSpace(entry.Sha256)))
                _fileHashCache[Path.GetFullPath(entry.Path)] = entry;
        }
        catch { }
    }

    private void LoadPilotHistory()
    {
        try
        {
            if (!Directory.Exists(_pilotCacheDirectory)) return;
            foreach (var path in Directory.EnumerateFiles(_pilotCacheDirectory, "pilot-report.json", SearchOption.AllDirectories))
            {
                try
                {
                    var result = JsonSerializer.Deserialize<PilotRunResult>(File.ReadAllText(path));
                    if (result is not null && !string.IsNullOrWhiteSpace(result.Signature)) _pilotHistory.Add(result);
                }
                catch { }
            }
            if (_pilotHistory.Count > 300)
            {
                var newest = _pilotHistory.OrderByDescending(result => result.CreatedAt).Take(300).ToArray();
                _pilotHistory.Clear();
                _pilotHistory.AddRange(newest);
            }
        }
        catch { }
    }

    private async Task<ToolchainSnapshot> ComputeToolchainSnapshotAsync(JobSettings settings, CancellationToken cancellationToken)
    {
        await _fingerprintGate.WaitAsync(cancellationToken);
        try
        {
            var components = new List<ToolchainComponent>();
            foreach (var path in CollectToolchainFiles(settings))
            {
                cancellationToken.ThrowIfCancellationRequested();
                components.Add(await GetToolchainComponentAsync(path, cancellationToken));
            }

            var environmentSignature = await QueryExecutionEnvironmentAsync(cancellationToken);
            var pipelineSignature = BuildPipelineSignature(settings);
            var hash = ToolchainFingerprintBuilder.Build(
                ToolchainFingerprintVersion,
                settings.Engine,
                pipelineSignature,
                environmentSignature,
                components);
            var snapshot = new ToolchainSnapshot(
                ToolchainFingerprintVersion,
                hash,
                settings.Engine,
                settings.ProfileCode,
                settings.ModelName ?? "",
                settings.Cq,
                settings.IsBeta,
                EffectiveBetaSignature(settings),
                pipelineSignature,
                environmentSignature,
                components.OrderBy(component => component.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(),
                DateTimeOffset.Now);
            SaveFileHashCache();
            SaveToolchainSnapshot(snapshot);
            return snapshot;
        }
        finally
        {
            _fingerprintGate.Release();
        }
    }

    private IReadOnlyList<string> CollectToolchainFiles(JobSettings settings)
    {
        var files = new List<string> { _ffmpeg, _ffprobe };
        if (settings.Engine == "animejanai")
        {
            files.Add(_ajiEncode);
            files.Add(_trtexec);
            files.AddRange(Directory.EnumerateFiles(_inferenceDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Contains("_dml", StringComparison.OrdinalIgnoreCase)));
            if (string.IsNullOrWhiteSpace(settings.ModelName))
                throw new InvalidOperationException("Modelo AnimeJaNai ausente ao calcular o fingerprint.");
            var modelPath = Path.Combine(_modelDirectory, settings.ModelName + ".onnx");
            files.Add(modelPath);
            files.AddRange(Directory.EnumerateFiles(_modelDirectory, settings.ModelName + ".*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".engine", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".timing.cache", StringComparison.OrdinalIgnoreCase)));
        }
        else if (!string.IsNullOrWhiteSpace(settings.ShaderPath))
        {
            files.Add(settings.ShaderPath);
        }

        var result = files
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var missing = result.Where(path => !File.Exists(path)).ToArray();
        if (missing.Length > 0)
            throw new FileNotFoundException("Componente da toolchain ausente: " + string.Join(", ", missing.Select(Path.GetFileName)));
        return result;
    }

    private async Task<ToolchainComponent> GetToolchainComponentAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (_fileHashCache.TryGetValue(fullPath, out var cached) &&
            cached.Length == file.Length && cached.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
        {
            if (_verifiedFileHashesThisSession.Contains(fullPath))
                return BuildToolchainComponent(fullPath, file.Length, cached.Sha256);
            var quickHash = await ComputeQuickFileHashAsync(fullPath, cancellationToken);
            if (string.Equals(quickHash, cached.QuickSha256, StringComparison.OrdinalIgnoreCase))
            {
                _verifiedFileHashesThisSession.Add(fullPath);
                return BuildToolchainComponent(fullPath, file.Length, cached.Sha256);
            }
        }

        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        var quick = await ComputeQuickFileHashAsync(fullPath, cancellationToken);
        var verifiedFile = new FileInfo(fullPath);
        if (verifiedFile.Length != file.Length || verifiedFile.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtc.Ticks)
            throw new IOException($"O componente {Path.GetFileName(fullPath)} mudou durante o cálculo do fingerprint; tente novamente.");
        _fileHashCache[fullPath] = new FileHashCacheEntry(
            fullPath,
            file.Length,
            file.LastWriteTimeUtc.Ticks,
            quick,
            digest,
            DateTimeOffset.Now);
        _verifiedFileHashesThisSession.Add(fullPath);
        return BuildToolchainComponent(fullPath, file.Length, digest);
    }

    private ToolchainComponent BuildToolchainComponent(string fullPath, long length, string sha256)
    {
        var relative = Path.GetRelativePath(_root, fullPath).Replace('\\', '/');
        return new ToolchainComponent(relative, length, sha256);
    }

    private static async Task<string> ComputeQuickFileHashAsync(string path, CancellationToken cancellationToken)
    {
        const int chunkSize = 64 * 1024;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(BitConverter.GetBytes(stream.Length));
        var buffer = new byte[chunkSize];
        var firstLength = (int)Math.Min(chunkSize, stream.Length);
        var firstRead = await ReadAtMostAsync(stream, buffer, firstLength, cancellationToken);
        hash.AppendData(buffer, 0, firstRead);
        if (stream.Length > chunkSize)
        {
            stream.Seek(Math.Max(chunkSize, stream.Length - chunkSize), SeekOrigin.Begin);
            var lastRead = await ReadAtMostAsync(stream, buffer, chunkSize, cancellationToken);
            hash.AppendData(buffer, 0, lastRead);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<int> ReadAtMostAsync(FileStream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private string BuildPipelineSignature(JobSettings settings)
    {
        IReadOnlyList<string> arguments;
        if (settings.Engine == "animejanai")
        {
            arguments = ApplyBetaOptions(
                BuildSegmentVideoArguments("{PIPE}", "{SEGMENT}", settings.Cq, AnimeJanaiOverlapFrames, 1000),
                settings.BetaOptions ?? new BetaFfmpegOptions());
        }
        else
        {
            arguments = ApplyBetaOptions(
                BuildFfmpegArguments("{INPUT}", "{OUTPUT}", settings.Cq, settings.ShaderPath),
                settings.BetaOptions ?? new BetaFfmpegOptions());
        }
        return string.Join("|",
            $"segmented={SegmentedPipelineVersion}",
            $"pilot={PilotPipelineVersion}",
            $"segmentSeconds={AnimeJanaiSegmentSeconds}",
            $"overlapFrames={AnimeJanaiOverlapFrames}",
            $"engine={settings.Engine}",
            $"profile={settings.ProfileCode}",
            $"model={settings.ModelName ?? ""}",
            $"cq={settings.Cq}",
            $"beta={(settings.IsBeta ? 1 : 0)}:{EffectiveBetaSignature(settings)}",
            "sourceStage=hevc_nvenc:p7:lossless:constqp0:fps_passthrough",
            "finalMux=video_copy:audio_aac_q2_48k:metadata_chapters_attachments_copy",
            string.Join(" ", arguments));
    }

    private async Task<string> QueryExecutionEnvironmentAsync(CancellationToken cancellationToken)
    {
        var gpu = "unavailable";
        try
        {
            var installedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            var executable = File.Exists(installedPath) ? installedPath : "nvidia-smi.exe";
            var args = new[] { "--query-gpu=name,driver_version,compute_cap", "--format=csv,noheader,nounits" };
            var info = CreateProcessInfo(executable, args, _root);
            using var process = Process.Start(info) ?? throw new InvalidOperationException();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            _ = await errorTask;
            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output)) gpu = Regex.Replace(output, @"\s+", " ");
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return $"os={RuntimeInformation.OSDescription};osarch={RuntimeInformation.OSArchitecture};processarch={RuntimeInformation.ProcessArchitecture};gpu={gpu}";
    }

    private void SaveFileHashCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_fileHashCachePath)!);
            var temporaryPath = _fileHashCachePath + ".tmp";
            var json = JsonSerializer.Serialize(_fileHashCache.Values.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, _fileHashCachePath, true);
        }
        catch { }
    }

    private void SaveToolchainSnapshot(ToolchainSnapshot snapshot)
    {
        Directory.CreateDirectory(_toolchainSnapshotsDirectory);
        var path = Path.Combine(_toolchainSnapshotsDirectory, snapshot.Hash + ".json");
        if (File.Exists(path)) return;
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }

    private void LoadHistory()
    {
        foreach (var path in new[] { _historyPath, _betaHistoryPath })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var records = JsonSerializer.Deserialize<List<HistoryRecord>>(File.ReadAllText(path));
                if (records is not null)
                    _history.AddRange(records
                        .Where(record => record.InputBytes > 0 && record.OutputBytes > 0)
                        .Select(record => !record.IsBeta ? record with { BetaSignature = "" } : record)
                        .TakeLast(500));
            }
            catch { }
        }
    }

    private void RecordHistory(HistoryRecord record)
    {
        lock (_historyLock)
        {
            _history.Add(record);
            var trackRecords = _history.Where(r => r.IsBeta == record.IsBeta).OrderBy(r => r.Timestamp).ToList();
            if (trackRecords.Count > 500)
            {
                var remove = trackRecords.Take(trackRecords.Count - 500).ToHashSet();
                _history.RemoveAll(r => r.IsBeta == record.IsBeta && remove.Contains(r));
                trackRecords = trackRecords.TakeLast(500).ToList();
            }

            var historyPath = record.IsBeta ? _betaHistoryPath : _historyPath;
            Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
            var json = JsonSerializer.Serialize(trackRecords, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(historyPath, json, new UTF8Encoding(false));
        }
    }

    private string CreateLogPath(string baseName, string jobId)
    {
        var directory = Path.Combine(_logsDirectory, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, SafeBaseName(baseName, 80) + "-" + jobId + ".log");
    }

    private static void AppendLog(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, text, new UTF8Encoding(false));
    }

    private static TimeSpan? ParseProgressTime(string line)
    {
        var match = Regex.Match(line, @"(?:time=|out_time=)(?<time>\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return TimeSpan.TryParse(match.Groups["time"].Value, CultureInfo.InvariantCulture, out var time) ? time : null;
    }

    private static double? ParseAnimeJanaiPercent(string line)
    {
        if (!line.Contains("phase=encode", StringComparison.OrdinalIgnoreCase)) return null;
        var match = Regex.Match(line, @"\bpct=(?<pct>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    private static string FriendlyError(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "O processo terminou sem fornecer detalhes. Consulte o log.";
        if (message.Contains("No option name near", StringComparison.OrdinalIgnoreCase)) return "O FFmpeg rejeitou o caminho do shader. Consulte o log.";
        if (message.Contains("No space left", StringComparison.OrdinalIgnoreCase)) return "O disco ficou sem espaço durante o processamento.";
        if (message.Contains("Cannot allocate memory", StringComparison.OrdinalIgnoreCase) || message.Contains("Failed to allocate packet", StringComparison.OrdinalIgnoreCase))
            return "O AnimeJaNai ficou sem memória no bloco atual. Os blocos já validados foram preservados; feche aplicações e inicie novamente para retomar.";
        if (message.Contains("CUDA", StringComparison.OrdinalIgnoreCase) || message.Contains("TensorRT", StringComparison.OrdinalIgnoreCase)) return "Falha no TensorRT/CUDA. Consulte o log para identificar o componente.";
        return Tail(message);
    }

    private static string Tail(string value, int max = 5000)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[^max..];
    }

    private static void KillProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
        }
        catch { }
    }

    private static string SafeBaseName(string value, int maxLength = 160)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(value.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "video";
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength].TrimEnd();
    }

    private static string Sanitize(string value) => SafeBaseName(value, 60).Replace(' ', '_');

    private string BuildSegmentedWorkDirectory(string destinationDirectory, EncodeItem item, JobSettings settings)
    {
        var file = new FileInfo(item.InputPath);
        var identity = string.Join("|", new[]
        {
            SegmentedPipelineVersion.ToString(CultureInfo.InvariantCulture),
            Path.GetFullPath(item.InputPath).ToUpperInvariant(),
            file.Length.ToString(CultureInfo.InvariantCulture),
            file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            settings.ProfileCode,
            settings.ModelName ?? "",
            settings.Cq.ToString(CultureInfo.InvariantCulture),
            settings.IsBeta ? "beta" : "stable",
            settings.BetaOptions?.Signature ?? "",
            AnimeJanaiSegmentSeconds.ToString(CultureInfo.InvariantCulture),
            AnimeJanaiOverlapFrames.ToString(CultureInfo.InvariantCulture)
        });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        var name = SafeBaseName(Path.GetFileNameWithoutExtension(item.InputPath), 70);
        return Path.Combine(destinationDirectory, ".anime-upscale-temp", $"resume-{name}-{hash}");
    }

    private static List<AnimeJanaiSegmentPlan> BuildSegmentPlan(long totalFrames, long framesPerSegment, int overlapFrames)
    {
        var result = new List<AnimeJanaiSegmentPlan>();
        var index = 0;
        for (long usefulStart = 0; usefulStart < totalFrames; usefulStart += framesPerSegment)
        {
            var usefulEnd = Math.Min(totalFrames, usefulStart + framesPerSegment);
            var sourceStart = Math.Max(0, usefulStart - overlapFrames);
            var sourceEnd = Math.Min(totalFrames, usefulEnd + overlapFrames);
            result.Add(new AnimeJanaiSegmentPlan(
                index++,
                usefulStart,
                usefulEnd,
                sourceStart,
                sourceEnd,
                usefulStart - sourceStart));
        }
        return result;
    }

    private static string SegmentFileName(int index) => $"segment-{index + 1:0000}.mkv";

    private static List<string> BuildLosslessSourceSegmentArguments(
        string inputPath,
        string outputPath,
        double startSeconds,
        long frameCount,
        string sourcePixelFormat)
    {
        var tenBit = sourcePixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) ||
                     sourcePixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-ss", startSeconds.ToString("0.#########", CultureInfo.InvariantCulture),
            "-i", inputPath,
            "-map", "0:v:0", "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture),
            "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1", "-vf", "setpts=PTS-STARTPTS",
            "-c:v", "hevc_nvenc", "-preset", "p7", "-tune", "lossless", "-rc", "constqp", "-qp", "0",
            "-pix_fmt", tenBit ? "p010le" : "yuv420p",
            "-fps_mode", "passthrough", "-avoid_negative_ts", "make_zero",
            "-progress", "pipe:1", "-nostats", outputPath
        ];
    }

    private static List<string> BuildPilotConcatArguments(string concatPath, string outputPath) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-f", "concat", "-safe", "0", "-i", concatPath,
        "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1", "-map_chapters", "-1",
        "-c:v", "copy", "-fflags", "+genpts", "-avoid_negative_ts", "make_zero",
        "-progress", "pipe:1", "-nostats", outputPath
    ];

    private static void WritePilotConcatManifest(string path, IReadOnlyList<string> sourcePaths)
    {
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (var sourcePath in sourcePaths)
        {
            var fileName = Path.GetFileName(sourcePath).Replace("'", "'\\''", StringComparison.Ordinal);
            content.Append("file '").Append(fileName).AppendLine("'");
        }
        File.WriteAllText(path, content.ToString(), new UTF8Encoding(false));
    }

    private static List<string> BuildSegmentVideoArguments(
        string inputPath,
        string outputPath,
        int cq,
        long prefixOverlapFrames,
        long usefulFrameCount)
    {
        var endFrame = prefixOverlapFrames + usefulFrameCount;
        return
        [
            "-hide_banner", "-nostdin", "-y", "-fflags", "+genpts", "-i", inputPath,
            "-map", "0:v:0",
            "-vf", $"trim=start_frame={prefixOverlapFrames}:end_frame={endFrame},setpts=PTS-STARTPTS",
            "-an", "-sn", "-dn",
            "-c:v:0", "av1_nvenc", "-preset", "p7", "-tune", "uhq", "-rc", "vbr", "-b:v", "0",
            "-cq", cq.ToString(CultureInfo.InvariantCulture), "-multipass", "fullres",
            "-rc-lookahead", "32", "-lookahead_level", "3", "-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8",
            "-pix_fmt", "p010le", "-highbitdepth", "1", "-fps_mode", "passthrough",
            "-avoid_negative_ts", "make_zero", "-max_interleave_delta", "0", "-max_muxing_queue_size", "4096",
            "-progress", "pipe:1", "-nostats", outputPath
        ];
    }

    private static List<string> BuildSegmentedFinalMuxArguments(
        string concatPath,
        string sourcePath,
        string outputPath,
        MediaStructure source)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-f", "concat", "-safe", "0", "-i", concatPath,
            "-i", sourcePath,
            "-map", "0:v:0", "-map", "1:a?", "-map", "1:s?", "-map", "1:t?",
            "-map_metadata", "1", "-map_chapters", "1", "-c", "copy",
            "-c:a", "aac", "-q:a", "2", "-ar:a", "48000", "-af:a", "aresample=async=1:first_pts=0",
            "-avoid_negative_ts", "make_zero", "-max_interleave_delta", "0", "-max_muxing_queue_size", "4096"
        };

        AppendStreamMetadataAndDisposition(args, source.VideoIdentity, "v:0");
        AppendStreamGroupMetadataAndDisposition(args, source.PreservedStreams, "audio", "a");
        AppendStreamGroupMetadataAndDisposition(args, source.PreservedStreams, "subtitle", "s");
        AppendStreamGroupMetadataAndDisposition(args, source.PreservedStreams, "attachment", "t");
        args.AddRange(["-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    private static void AppendStreamGroupMetadataAndDisposition(
        List<string> args,
        IReadOnlyList<StreamIdentity> streams,
        string type,
        string specifier)
    {
        var index = 0;
        foreach (var stream in streams.Where(stream => stream.Type == type))
            AppendStreamMetadataAndDisposition(args, stream, $"{specifier}:{index++}");
    }

    private static void AppendStreamMetadataAndDisposition(List<string> args, StreamIdentity stream, string specifier)
    {
        args.AddRange(["-disposition:" + specifier, stream.Disposition]);
        args.AddRange(["-metadata:s:" + specifier, "language=" + stream.Language]);
        args.AddRange(["-metadata:s:" + specifier, "title=" + stream.Title]);
        if (stream.Type == "attachment")
        {
            args.AddRange(["-metadata:s:" + specifier, "filename=" + stream.FileName]);
            args.AddRange(["-metadata:s:" + specifier, "mimetype=" + stream.MimeType]);
        }
    }

    private static void WriteConcatManifest(string path, IReadOnlyList<AnimeJanaiSegmentPlan> segments)
    {
        var content = new StringBuilder("ffconcat version 1.0\n");
        foreach (var segment in segments)
            content.Append("file '").Append(SegmentFileName(segment.Index)).AppendLine("'");
        File.WriteAllText(path, content.ToString(), new UTF8Encoding(false));
    }

    private SegmentManifest LoadOrCreateSegmentManifest(
        string path,
        EncodeItem item,
        JobSettings settings,
        MediaStructure source,
        IReadOnlyList<AnimeJanaiSegmentPlan> segments,
        string toolchainFingerprint)
    {
        SegmentManifest? manifest = null;
        try
        {
            if (File.Exists(path))
                manifest = JsonSerializer.Deserialize<SegmentManifest>(File.ReadAllText(path));
        }
        catch { }

        var sourceFile = new FileInfo(item.InputPath);
        var compatible = manifest is not null &&
            manifest.Version == SegmentedPipelineVersion &&
            string.Equals(manifest.SourcePath, Path.GetFullPath(item.InputPath), StringComparison.OrdinalIgnoreCase) &&
            manifest.SourceLength == sourceFile.Length &&
            manifest.SourceLastWriteUtcTicks == sourceFile.LastWriteTimeUtc.Ticks &&
            manifest.TotalFrames == source.VideoPackets &&
            Math.Abs(manifest.FrameRate - source.AverageFrameRate) < 0.000001 &&
            manifest.ProfileCode == settings.ProfileCode &&
            manifest.ModelName == (settings.ModelName ?? "") &&
            manifest.Cq == settings.Cq &&
            manifest.IsBeta == settings.IsBeta &&
            manifest.BetaSignature == EffectiveBetaSignature(settings) &&
            manifest.ToolchainFingerprint == toolchainFingerprint &&
            manifest.SegmentSeconds == AnimeJanaiSegmentSeconds &&
            manifest.OverlapFrames == AnimeJanaiOverlapFrames &&
            manifest.Segments.Count == segments.Count &&
            manifest.Segments.Zip(segments).All(pair =>
                pair.First.Index == pair.Second.Index &&
                pair.First.UsefulStartFrame == pair.Second.UsefulStartFrame &&
                pair.First.UsefulEndFrame == pair.Second.UsefulEndFrame &&
                pair.First.SourceStartFrame == pair.Second.SourceStartFrame &&
                pair.First.SourceEndFrame == pair.Second.SourceEndFrame);

        if (!compatible)
        {
            manifest = new SegmentManifest
            {
                Version = SegmentedPipelineVersion,
                SourcePath = Path.GetFullPath(item.InputPath),
                SourceLength = sourceFile.Length,
                SourceLastWriteUtcTicks = sourceFile.LastWriteTimeUtc.Ticks,
                TotalFrames = source.VideoPackets,
                FrameRate = source.AverageFrameRate,
                ProfileCode = settings.ProfileCode,
                ModelName = settings.ModelName ?? "",
                Cq = settings.Cq,
                IsBeta = settings.IsBeta,
                BetaSignature = EffectiveBetaSignature(settings),
                ToolchainFingerprint = toolchainFingerprint,
                SegmentSeconds = AnimeJanaiSegmentSeconds,
                OverlapFrames = AnimeJanaiOverlapFrames,
                CreatedAt = DateTimeOffset.Now,
                Segments = segments.Select(s => new SegmentCheckpoint
                {
                    Index = s.Index,
                    UsefulStartFrame = s.UsefulStartFrame,
                    UsefulEndFrame = s.UsefulEndFrame,
                    SourceStartFrame = s.SourceStartFrame,
                    SourceEndFrame = s.SourceEndFrame
                }).ToList()
            };
            SaveSegmentManifest(path, manifest);
        }
        return manifest!;
    }

    private static void SaveSegmentManifest(string path, SegmentManifest manifest)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, true);
    }

    private List<string> BuildFfmpegArguments(string inputPath, string outputPath, int cq, string? shaderPath)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-y" };
        if (!string.IsNullOrWhiteSpace(shaderPath))
        {
            var filterPath = shaderPath.Replace('\\', '/').Replace(":", "\\:");
            args.AddRange([
                "-init_hw_device", "vulkan=vulkan", "-filter_hw_device", "vulkan",
                "-fflags", "+genpts", "-i", inputPath,
                "-vf", $"libplacebo=w=3840:h=2160:upscaler=ewa_lanczos:custom_shader_path='{filterPath}'"]);
        }
        else
        {
            args.AddRange(["-fflags", "+genpts", "-i", inputPath]);
        }

        args.AddRange([
            "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-map", "0:t?",
            "-map_metadata", "0", "-map_chapters", "0", "-c", "copy",
            "-c:v:0", "av1_nvenc", "-preset", "p7", "-tune", "uhq", "-rc", "vbr", "-b:v", "0",
            "-cq", cq.ToString(CultureInfo.InvariantCulture), "-multipass", "fullres",
            "-rc-lookahead", "32", "-lookahead_level", "3", "-spatial-aq", "1", "-temporal-aq", "1", "-aq-strength", "8",
            "-pix_fmt", "p010le", "-highbitdepth", "1",
            "-c:a", "aac", "-q:a", "2", "-ar:a", "48000", "-af:a", "aresample=async=1:first_pts=0",
            "-avoid_negative_ts", "make_zero", "-max_interleave_delta", "0", "-max_muxing_queue_size", "4096",
            "-progress", "pipe:1", "-nostats", outputPath]);
        return args;
    }

    private static List<string> ApplyBetaOptions(IReadOnlyList<string> source, BetaFfmpegOptions options)
    {
        var args = source.ToList();
        if (!options.AnyEnabled) return args;

        if (options.LookaheadLevel4) SetFfmpegOption(args, "-lookahead_level", "4");
        if (options.AqStrength10) SetFfmpegOption(args, "-aq-strength", "10");
        if (options.BAdapt) SetFfmpegOption(args, "-b_adapt", "1");
        if (options.BRefHierarchical) SetFfmpegOption(args, "-b_ref_mode", "hierarchical");
        if (options.WeightedPrediction) SetFfmpegOption(args, "-weighted_pred", "1");
        return args;
    }

    private static void SetFfmpegOption(List<string> args, string option, string value)
    {
        var index = args.FindIndex(a => string.Equals(a, option, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Count) args[index + 1] = value;
        else
        {
            var outputIndex = Math.Max(0, args.Count - 1);
            args.Insert(outputIndex, option);
            args.Insert(outputIndex + 1, value);
        }
    }

    private List<string> ApplyAdvancedFfmpegCommand(IReadOnlyList<string> fallback, string? command, string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(command)) return fallback.ToList();
        var tokens = SplitCommandLine(command);
        if (tokens.Count < 2) throw new InvalidOperationException("O comando avançado está vazio ou incompleto.");

        if (tokens[0] == "&") tokens.RemoveAt(0);
        if (tokens.Count < 2) throw new InvalidOperationException("O comando avançado está vazio ou incompleto.");
        var executableToken = tokens[0].Trim().TrimStart('&').Trim();
        var executableName = Path.GetFileName(executableToken).Trim('"');
        if (!string.Equals(executableName, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(executableName, "ffmpeg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("O comando avançado precisa começar com ffmpeg.exe. O caminho informado será ignorado e o FFmpeg do Studio será usado.");

        var args = tokens.Skip(1).Select(token => token
            .Replace("{INPUT}", inputPath, StringComparison.Ordinal)
            .Replace("{OUTPUT}", outputPath, StringComparison.Ordinal)).ToList();
        if (!args.Contains("-i", StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("O comando avançado precisa conter a opção -i.");
        return args;
    }

    private static List<string> SplitCommandLine(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string BuildPilotSignature(EncodeItem item, string engine, string profile, int cq, bool isBeta, string betaSignature)
    {
        try
        {
            var source = new FileInfo(item.InputPath);
            var value = string.Join("|",
                PilotPipelineVersion.ToString(CultureInfo.InvariantCulture),
                Path.GetFullPath(item.InputPath).ToUpperInvariant(),
                source.Length.ToString(CultureInfo.InvariantCulture),
                source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture),
                engine,
                profile,
                cq.ToString(CultureInfo.InvariantCulture),
                isBeta ? "1" : "0",
                betaSignature);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
        catch
        {
            return "";
        }
    }

    private static string FormatBitRate(long bitRate) => bitRate >= 1_000_000
        ? $"{bitRate / 1_000_000d:0.##} Mb/s"
        : $"{bitRate / 1_000d:0.##} kb/s";

    private static string BuildPilotSummary(PilotRunResult result)
    {
        var range = $"{FormatDuration(TimeSpan.FromSeconds(result.MinimumProjectedSeconds))}–{FormatDuration(TimeSpan.FromSeconds(result.MaximumProjectedSeconds))}";
        var audio = result.EstimatedAudioBitRate > 0 ? $" • áudio estimado {FormatBitRate(result.EstimatedAudioBitRate)}" : "";
        var fingerprint = string.IsNullOrWhiteSpace(result.ToolchainFingerprint) ? "legado" : result.ToolchainFingerprint[..Math.Min(12, result.ToolchainFingerprint.Length)];
        return $"{result.SampleCount}×{result.SampleSeconds / result.SampleCount:0.#} s • velocidade {result.SpeedFactor:0.###}x • " +
               $"tempo projetado {FormatDuration(TimeSpan.FromSeconds(result.ProjectedSeconds))} (faixa {range}) • " +
               $"vídeo {FormatBitRate(result.VideoBitRate)}{audio} • bitrate final {FormatBitRate(result.ProjectedBitRate)} • " +
               $"tamanho {FormatBytes(result.ProjectedOutputBytes)} • toolchain {fingerprint}";
    }

    private void ShowPilotResult(PilotRunResult result)
    {
        PilotSummaryText.Text = BuildPilotSummary(result);
        PilotSummaryPanel.Visibility = Visibility.Visible;
        OpenPilotButton.IsEnabled = File.Exists(result.PreviewPath);
        ComparePilotButton.IsEnabled = HasComparison(result);
        ComparePilotButton.ToolTip = HasComparison(result) ? "Comparar os mesmos quadros com divisória e zoom" : "Execute um novo piloto para gerar a comparação";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return duration.TotalHours >= 1
            ? $"{(long)Math.Floor(duration.TotalHours):00}:{duration.Minutes:00}:{duration.Seconds:00}"
            : duration.ToString(@"mm\:ss");
    }

    private static string FormatCommand(string executable, IEnumerable<string> arguments) =>
        Quote(executable) + " " + string.Join(" ", arguments.Select(Quote));

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? "\"" + value.Replace("\"", "\\\"") + "\""
        : value;

    private static string GetJsonString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static double ParseJsonDouble(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static long ParseJsonLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static int ParseJsonInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : 0;

    private static string GetNestedJsonString(JsonElement element, string container, string property) =>
        element.TryGetProperty(container, out var nested) && nested.TryGetProperty(property, out var value)
            ? value.GetString() ?? ""
            : "";

    private static StreamIdentity BuildStreamIdentity(JsonElement stream, string type) =>
        new(
            type,
            NormalizeLanguageTag(GetNestedJsonString(stream, "tags", "language")),
            GetNestedJsonString(stream, "tags", "title"),
            GetNestedJsonString(stream, "tags", "filename"),
            GetNestedJsonString(stream, "tags", "mimetype"),
            GetDisposition(stream));

    private static string NormalizeLanguageTag(string language) =>
        string.IsNullOrWhiteSpace(language) ? "und" : language.Trim().ToLowerInvariant();

    private static string GetDisposition(JsonElement stream)
    {
        if (!stream.TryGetProperty("disposition", out var disposition)) return "0";
        var active = disposition.EnumerateObject()
            .Where(property => property.Value.TryGetInt32(out var value) && value != 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        return active.Length == 0 ? "0" : string.Join("+", active);
    }

    private static string DescribeStreamIdentityMismatch(
        string type,
        int index,
        StreamIdentity expected,
        StreamIdentity actual)
    {
        var label = type switch
        {
            "audio" => "Áudio",
            "subtitle" => "Legenda",
            "attachment" => "Anexo",
            _ => "Vídeo"
        };
        return $"{label} {index + 1} difere do arquivo original. " +
               $"Esperado: idioma='{expected.Language}', título='{expected.Title}', " +
               $"arquivo='{expected.FileName}', MIME='{expected.MimeType}', disposição='{expected.Disposition}'. " +
               $"Recebido: idioma='{actual.Language}', título='{actual.Title}', " +
               $"arquivo='{actual.FileName}', MIME='{actual.MimeType}', disposição='{actual.Disposition}'.";
    }

    private static long ParseJsonLongFlexible(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)) return number;
        return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseRational(string value)
    {
        var parts = value.Split('/', 2);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            Math.Abs(denominator) < double.Epsilon)
            return 0;
        return numerator / denominator;
    }
}

public interface IProcessingProfile
{
    string Name { get; }
    string Code { get; }
}

public sealed record EngineOption(string Name, string Code);
public sealed record ShaderPreset(string Name, string Code, string[] Files) : IProcessingProfile;
public sealed record AnimeJanaiPreset(
    string Name,
    string Code,
    string ModelName,
    bool IsLegacyExperimental = false,
    string Warning = "") : IProcessingProfile;
public sealed record BetaFfmpegOptions(
    bool LookaheadLevel4 = false,
    bool AqStrength10 = false,
    bool BAdapt = false,
    bool BRefHierarchical = false,
    bool WeightedPrediction = false)
{
    public bool AnyEnabled => LookaheadLevel4 || AqStrength10 || BAdapt || BRefHierarchical || WeightedPrediction;
    public string Signature => $"la4={(LookaheadLevel4 ? 1 : 0)};aq10={(AqStrength10 ? 1 : 0)};badapt={(BAdapt ? 1 : 0)};bref={(BRefHierarchical ? 1 : 0)};wp={(WeightedPrediction ? 1 : 0)}";
}
public sealed record JobSettings(
    string Engine,
    string ProfileCode,
    string ProfileName,
    int Cq,
    int Concurrency,
    string OutputDirectory,
    string? ShaderPath,
    string? ModelName,
    string? AdvancedFfmpegCommand = null,
    bool IsBeta = false,
    BetaFfmpegOptions? BetaOptions = null);
public sealed record HistoryRecord(
    DateTimeOffset Timestamp,
    string Engine,
    string Profile,
    int Cq,
    long InputBytes,
    long OutputBytes,
    double DurationSeconds,
    double ElapsedSeconds,
    int InputWidth = 1920,
    int InputHeight = 1080,
    long InputBitRate = 0,
    int AudioTracks = 0,
    bool HasAssSubtitle = false,
    string InputVideoCodec = "",
    string InputPixelFormat = "",
    int Concurrency = 1,
    bool IsBeta = false,
    string BetaSignature = "",
    int PipelineVersion = 0,
    int SegmentSeconds = 0);
public sealed record AdaptiveEstimate(long OutputBytes, double TotalSeconds, int SampleCount, double Confidence, string Basis);
public sealed record PilotSamplePlan(int Index, long StartFrame, long FrameCount);
public sealed record PilotProjection(
    double SpeedFactor,
    long VideoBitRate,
    long ProjectedBitRate,
    long ProjectedOutputBytes,
    double ProjectedSeconds,
    double MinimumProjectedSeconds,
    double MaximumProjectedSeconds);
public sealed record PilotRunResult(
    int Version,
    string Signature,
    string ToolchainFingerprint,
    string InputPath,
    string Engine,
    string ProfileCode,
    string ProfileName,
    int Cq,
    bool IsBeta,
    string BetaSignature,
    int SampleCount,
    double SampleSeconds,
    double CoreElapsedSeconds,
    double SpeedFactor,
    long VideoBitRate,
    long EstimatedAudioBitRate,
    long ProjectedBitRate,
    long ProjectedOutputBytes,
    double ProjectedSeconds,
    double MinimumProjectedSeconds,
    double MaximumProjectedSeconds,
    string PreviewPath,
    string LogPath,
    DateTimeOffset CreatedAt,
    PilotComparisonResult? Comparison = null);
public sealed record FileHashCacheEntry(
    string Path,
    long Length,
    long LastWriteUtcTicks,
    string QuickSha256,
    string Sha256,
    DateTimeOffset VerifiedAt);
public sealed record ToolchainComponent(string RelativePath, long Length, string Sha256);
public sealed record ToolchainSnapshot(
    int Version,
    string Hash,
    string Engine,
    string ProfileCode,
    string ModelName,
    int Cq,
    bool IsBeta,
    string BetaSignature,
    string PipelineSignature,
    string EnvironmentSignature,
    IReadOnlyList<ToolchainComponent> Components,
    DateTimeOffset CreatedAt);
public static class ToolchainFingerprintBuilder
{
    public static string Build(
        int version,
        string engine,
        string pipelineSignature,
        string environmentSignature,
        IEnumerable<ToolchainComponent> components)
    {
        var canonical = new StringBuilder()
            .Append("version=").Append(version.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append("engine=").Append(engine.Trim().ToLowerInvariant()).Append('\n')
            .Append("pipeline=").Append(pipelineSignature).Append('\n')
            .Append("environment=").Append(environmentSignature).Append('\n');
        foreach (var component in components.OrderBy(component => component.RelativePath, StringComparer.OrdinalIgnoreCase))
            canonical.Append(component.RelativePath.Replace('\\', '/').ToLowerInvariant()).Append('|')
                .Append(component.Length.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(component.Sha256.ToUpperInvariant()).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
public static class PilotPlanner
{
    public const int DesiredSampleCount = 3;
    public const double SecondsPerSample = 5;
    public const double ProjectionMargin = 0.15;

    public static IReadOnlyList<PilotSamplePlan> BuildSamples(long totalFrames, double frameRate)
    {
        if (totalFrames <= 0) throw new ArgumentOutOfRangeException(nameof(totalFrames));
        if (frameRate <= 0 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
            throw new ArgumentOutOfRangeException(nameof(frameRate));

        var sampleFrames = Math.Min(totalFrames, Math.Max(1L, (long)Math.Round(frameRate * SecondsPerSample, MidpointRounding.AwayFromZero)));
        var sampleCount = (int)Math.Min(DesiredSampleCount, Math.Max(1L, totalFrames / sampleFrames));
        var anchors = sampleCount switch
        {
            1 => new[] { 0.50 },
            2 => new[] { 0.25, 0.75 },
            _ => new[] { 0.18, 0.50, 0.82 }
        };
        var result = new List<PilotSamplePlan>(sampleCount);
        long previousEnd = 0;
        for (var index = 0; index < sampleCount; index++)
        {
            var preferred = (long)Math.Round(totalFrames * anchors[index] - sampleFrames / 2d, MidpointRounding.AwayFromZero);
            var maximumStart = totalFrames - (sampleCount - index) * sampleFrames;
            var start = Math.Clamp(preferred, previousEnd, Math.Max(previousEnd, maximumStart));
            result.Add(new PilotSamplePlan(index, start, sampleFrames));
            previousEnd = start + sampleFrames;
        }
        return result;
    }

    public static PilotProjection CalculateProjection(
        string engine,
        double sampleSeconds,
        double coreElapsedSeconds,
        long videoPacketBytes,
        double sourceDurationSeconds,
        long estimatedAudioBitRate)
    {
        if (sampleSeconds <= 0 || coreElapsedSeconds <= 0 || videoPacketBytes <= 0 || sourceDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleSeconds), "As métricas do piloto precisam ser positivas.");

        var videoBitRate = Math.Max(1L, (long)Math.Round(videoPacketBytes * 8d / sampleSeconds, MidpointRounding.AwayFromZero));
        const double containerOverhead = 1.007;
        var projectedBitRate = Math.Max(1L, (long)Math.Round((videoBitRate + Math.Max(0, estimatedAudioBitRate)) * containerOverhead, MidpointRounding.AwayFromZero));
        var projectedBytes = Math.Max(1L, (long)Math.Round(projectedBitRate * sourceDurationSeconds / 8d, MidpointRounding.AwayFromZero));
        var pipelineOverhead = string.Equals(engine, "animejanai", StringComparison.OrdinalIgnoreCase) ? 1.08 : 1.03;
        var projectedSeconds = coreElapsedSeconds / sampleSeconds * sourceDurationSeconds * pipelineOverhead;
        return new PilotProjection(
            sampleSeconds / coreElapsedSeconds,
            videoBitRate,
            projectedBitRate,
            projectedBytes,
            projectedSeconds,
            projectedSeconds * (1 - ProjectionMargin),
            projectedSeconds * (1 + ProjectionMargin));
    }
}
public sealed record MediaInfo(TimeSpan Duration, long Size, long BitRate, string VideoCodec, string PixelFormat, int Width, int Height, bool HasAssSubtitle, IReadOnlyList<string> AudioCodecs, long EstimatedAudioOutputBitRate);
public sealed record MediaStructure(
    double DurationSeconds,
    string VideoCodec,
    string PixelFormat,
    int Width,
    int Height,
    double AverageFrameRate,
    double NominalFrameRate,
    long VideoPackets,
    int AudioStreams,
    int SubtitleStreams,
    int AttachmentStreams,
    StreamIdentity VideoIdentity,
    IReadOnlyList<StreamIdentity> PreservedStreams,
    IReadOnlyList<ChapterIdentity> ChapterEntries)
{
    public int Chapters => ChapterEntries.Count;
}
public sealed record StreamIdentity(
    string Type,
    string Language,
    string Title,
    string FileName,
    string MimeType,
    string Disposition)
{
    public static StreamIdentity Empty(string type) => new(type, "", "", "", "", "0");
}
public sealed record ChapterIdentity(double StartSeconds, double EndSeconds, string Title);
public sealed record VideoTimeline(long PacketCount, double FirstPts, double LastPts, double MinimumStep, double MaximumStep);
public sealed record AnimeJanaiSegmentPlan(
    int Index,
    long UsefulStartFrame,
    long UsefulEndFrame,
    long SourceStartFrame,
    long SourceEndFrame,
    long PrefixOverlapFrames)
{
    public long UsefulFrameCount => UsefulEndFrame - UsefulStartFrame;
    public long SourceFrameCount => SourceEndFrame - SourceStartFrame;
}
public sealed class SegmentManifest
{
    public int Version { get; set; }
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public long TotalFrames { get; set; }
    public double FrameRate { get; set; }
    public string ProfileCode { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int Cq { get; set; }
    public bool IsBeta { get; set; }
    public string BetaSignature { get; set; } = "";
    public string ToolchainFingerprint { get; set; } = "";
    public int SegmentSeconds { get; set; }
    public int OverlapFrames { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<SegmentCheckpoint> Segments { get; set; } = [];
}
public sealed class SegmentCheckpoint
{
    public int Index { get; set; }
    public long UsefulStartFrame { get; set; }
    public long UsefulEndFrame { get; set; }
    public long SourceStartFrame { get; set; }
    public long SourceEndFrame { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long OutputBytes { get; set; }
}

public sealed class EncodeItem : INotifyPropertyChanged
{
    public string InputPath { get; }
    public string FileName => Path.GetFileName(InputPath);
    private string _outputPath = "";
    public string OutputPath { get => _outputPath; set { _outputPath = value; Notify(); Notify(nameof(CanOpenOutput)); } }
    public bool CanOpenOutput => Status == "Concluído" && !IsRunning && !string.IsNullOrWhiteSpace(OutputPath);
    public string Error { get; set; } = "";
    public string LogPath { get; set; } = "";
    public long InputBytes { get; private set; }
    public long InputBitRate { get; private set; }
    public int InputWidth { get; private set; }
    public int InputHeight { get; private set; }
    public int AudioTracks { get; private set; }
    public long EstimatedAudioOutputBitRate { get; private set; }
    public bool HasAssSubtitle { get; private set; }
    public string InputVideoCodec { get; private set; } = "";
    public string InputPixelFormat { get; private set; } = "";
    public long EstimatedOutputBytes { get => _estimatedOutputBytes; set { _estimatedOutputBytes = value; Notify(); Notify(nameof(EstimatedSizeText)); } }
    public long ActualOutputBytes { get => _actualOutputBytes; set { _actualOutputBytes = value; Notify(); Notify(nameof(ActualSizeText)); Notify(nameof(SizeComparisonText)); } }
    public double EstimatedTotalSeconds { get; set; }
    public string EstimateBasis { get; set; } = "estimativa padrão";
    public TimeSpan Elapsed { get => _elapsed; private set { _elapsed = value; Notify(); Notify(nameof(ElapsedText)); Notify(nameof(EtaText)); } }
    public DateTimeOffset? StartedAt { get; set; }

    private TimeSpan _duration;
    private TimeSpan _elapsed;
    private TimeSpan _processed;
    private double _progress;
    private long _estimatedOutputBytes;
    private long _actualOutputBytes;
    private string _status = "Analisando";
    private string _stage = "Análise";
    private string _speed = "";
    private string _compatibility = "Aguardando";
    private bool _isRunning;

    public TimeSpan Duration { get => _duration; set { _duration = value; Notify(); Notify(nameof(DurationText)); } }
    public TimeSpan Processed { get => _processed; set { _processed = value; Notify(); } }
    public double Progress { get => _progress; set { _progress = value; Notify(); Notify(nameof(ProgressText)); Notify(nameof(EtaText)); } }
    public string Status { get => _status; set { _status = value; Notify(); Notify(nameof(CanOpenOutput)); } }
    public string Stage { get => _stage; set { _stage = value; Notify(); Notify(nameof(ProgressText)); } }
    public string Speed { get => _speed; set { _speed = value; Notify(); Notify(nameof(ProgressText)); } }
    public string Compatibility { get => _compatibility; set { _compatibility = value; Notify(); } }
    public bool IsRunning { get => _isRunning; set { _isRunning = value; Notify(); Notify(nameof(EtaText)); Notify(nameof(CanOpenOutput)); } }
    public string DurationText => Duration == TimeSpan.Zero ? "—" : Duration.ToString(@"hh\:mm\:ss");
    public string ElapsedText => FormatElapsed(Elapsed);
    public string EstimatedSizeText => EstimatedOutputBytes <= 0 ? "—" : FormatBytesLocal(EstimatedOutputBytes);
    public string ActualSizeText => ActualOutputBytes <= 0 ? "Aguardando" : FormatBytesLocal(ActualOutputBytes);
    public string SizeComparisonText
    {
        get
        {
            if (ActualOutputBytes <= 0) return "O tamanho real será informado após a conclusão e validação.";
            if (EstimatedOutputBytes <= 0) return $"Tamanho final: {ActualSizeText}";
            var difference = (ActualOutputBytes - EstimatedOutputBytes) / (double)EstimatedOutputBytes * 100;
            var direction = difference >= 0 ? "acima" : "abaixo";
            return $"Estimado: {EstimatedSizeText} • Final: {ActualSizeText} • {Math.Abs(difference):0.0}% {direction} da estimativa";
        }
    }
    public string ProgressText => $"{Progress:0.0}% • {Stage}" + (string.IsNullOrWhiteSpace(Speed) ? "" : $" • {Speed}");
    public double RemainingEstimateSeconds
    {
        get
        {
            if (Status == "Concluído") return 0;
            if (IsRunning && StartedAt.HasValue && Progress > 1)
            {
                var elapsed = (DateTimeOffset.Now - StartedAt.Value).TotalSeconds;
                return Math.Max(0, elapsed * (100 - Progress) / Progress);
            }
            return Math.Max(0, EstimatedTotalSeconds * (1 - Progress / 100));
        }
    }
    public string EtaText
    {
        get
        {
            if (Status == "Concluído") return "00:00";
            if (Duration == TimeSpan.Zero) return "—";
            var eta = TimeSpan.FromSeconds(RemainingEstimateSeconds);
            return eta.TotalSeconds > 0 ? (eta.TotalHours >= 1 ? eta.ToString(@"hh\:mm\:ss") : eta.ToString(@"mm\:ss")) : "Calculando…";
        }
    }

    public EncodeItem(string inputPath) => InputPath = inputPath;

    public void UpdateElapsed(DateTimeOffset now)
    {
        if (StartedAt.HasValue && IsRunning)
            Elapsed = now - StartedAt.Value;
        else
            Notify(nameof(ElapsedText));
    }

    public void FinishTiming(DateTimeOffset now)
    {
        if (StartedAt.HasValue) Elapsed = now - StartedAt.Value;
    }

    public void ApplyMediaInfo(MediaInfo media)
    {
        Duration = media.Duration;
        InputBytes = media.Size > 0 ? media.Size : new FileInfo(InputPath).Length;
        InputBitRate = media.BitRate;
        InputWidth = media.Width;
        InputHeight = media.Height;
        AudioTracks = media.AudioCodecs.Count;
        EstimatedAudioOutputBitRate = media.EstimatedAudioOutputBitRate;
        HasAssSubtitle = media.HasAssSubtitle;
        InputVideoCodec = media.VideoCodec;
        InputPixelFormat = media.PixelFormat;
    }

    public void UpdateProgress()
    {
        Notify(nameof(EtaText));
        Notify(nameof(ProgressText));
    }

    private static string FormatBytesLocal(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string FormatElapsed(TimeSpan elapsed) => elapsed.TotalHours >= 1
        ? elapsed.ToString(@"hh\:mm\:ss")
        : elapsed.ToString(@"mm\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
