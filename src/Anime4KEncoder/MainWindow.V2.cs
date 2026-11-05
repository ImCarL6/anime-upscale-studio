using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Anime4KEncoder;

/// <summary>Ligações da interface 2.0: cartão em andamento, seletor de modo, painel recolhível e arrastar arquivos.</summary>
public partial class MainWindow
{
    private static readonly string[] DroppableVideoExtensions = [".mkv", ".mp4", ".webm"];
    private bool _syncingResourceSegments;

    private sealed record UiSettings(bool SettingsPanelVisible = true);

    private string UiSettingsPath => Path.Combine(_dataRoot, "data", "ui-settings.json");

    private void InitializeV2Ui()
    {
        try
        {
            if (File.Exists(UiSettingsPath) && JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath)) is { } settings)
                ApplySettingsPanelVisibility(settings.SettingsPanelVisible);
        }
        catch { /* Preferência visual: sem ela, o painel fica aberto. */ }
        // Formata números das ligações (ex.: 42,3%) no idioma do Windows, como o restante da interface.
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(System.Globalization.CultureInfo.CurrentCulture.IetfLanguageTag);
        SyncResourceSegments();
        RefreshNowCard();
    }

    private void ApplySettingsPanelVisibility(bool visible)
    {
        SettingsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SettingsToggleButton.Foreground = (System.Windows.Media.Brush)FindResource(visible ? "AccentTextBrush" : "TextBrush");
    }

    private void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        var visible = SettingsPanel.Visibility != Visibility.Visible;
        ApplySettingsPanelVisibility(visible);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(new UiSettings(visible)));
        }
        catch { /* Falha ao salvar a preferência não afeta o processamento. */ }
    }

    // O ComboBox oculto continua sendo a fonte do modo; os botões só o espelham.
    private void ResourceSegment_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingResourceSegments || sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        if (ResourceModeCombo.SelectedIndex != index) ResourceModeCombo.SelectedIndex = index;
    }

    private void SyncResourceSegments()
    {
        if (ResourceWatchRadio is null) return;
        _syncingResourceSegments = true;
        try
        {
            var target = ResourceModeCombo.SelectedIndex switch { 0 => ResourceWatchRadio, 2 => ResourceMaxRadio, _ => ResourceBalancedRadio };
            if (target.IsChecked != true) target.IsChecked = true;
        }
        finally { _syncingResourceSegments = false; }
    }

    private void RefreshNowCard()
    {
        var item = _activePilotItem ?? Items.FirstOrDefault(i => i.IsRunning);
        if (item is not null && !ReferenceEquals(NowCard.DataContext, item)) NowCard.DataContext = item;
        NowCard.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        NowLabel.Text = _activePilotItem is not null ? "Teste de qualidade" : "Processando agora";
        if (item is null || _resourceProtection is null) ResourceChips.Visibility = Visibility.Collapsed;

        var completed = Items.Count(i => i.Status == "Concluído");
        QueueCountText.Text = Items.Count == 0 ? "" : $"{completed} de {Items.Count} concluídos";
    }

    private void UpdateResourceChips(ResourceSnapshot snapshot)
    {
        CpuChipText.Text = $"CPU até {ResourceProtection.CpuPercent(ReadResourcePercent())}%";
        RamChipText.Text = $"RAM livre {FormatBytes((long)snapshot.AvailableRam)}";
        VramChipText.Text = snapshot.Gpus.Count > 0
            ? "VRAM " + string.Join(" / ", snapshot.Gpus.Select(gpu => $"{100d * gpu.Used / gpu.Total:0}%"))
            : "VRAM —";
        ResourceChips.Visibility = NowCard.Visibility;
    }

    private async Task AddDroppedFilesAsync(string[] paths)
    {
        string[] files;
        try
        {
            files = paths
                .SelectMany(path => Directory.Exists(path) ? Directory.EnumerateFiles(path) : [path])
                .Where(path => File.Exists(path) && DroppableVideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            FooterText.Text = "Não foi possível ler os itens arrastados: " + ex.Message;
            return;
        }
        if (files.Length == 0)
        {
            FooterText.Text = "Nenhum vídeo .mkv, .mp4 ou .webm encontrado nos itens arrastados.";
            return;
        }
        _addingFiles++;
        try { await AddInputsAsync(files); }
        finally { _addingFiles--; SaveSession(); }
    }
}
