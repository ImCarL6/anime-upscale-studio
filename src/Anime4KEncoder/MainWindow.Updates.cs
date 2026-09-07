using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Velopack;
using Velopack.Sources;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private UpdateManager? _updates;
    private UpdateInfo? _availableUpdate;
    private bool _checkingUpdates;
    private bool _downloadingUpdate;
    private bool _applyingUpdate;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };

    private void InitializeUpdates()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        UpdateStatusText.Text = $"v{version?.ToString(3)}";
        Loaded += async (_, _) =>
        {
            try
            {
                _updates = new UpdateManager(new GithubSource("https://github.com/ImCarL6/anime-upscale-studio", null, false));
                await CheckUpdatesAsync();
                _updateTimer.Start();
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = "Atualizações indisponíveis nesta execução.";
                LogUpdateError(ex);
            }
        };
        _updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        Closing += (_, e) =>
        {
            if (_queueCancellation is not null && !_applyingUpdate)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Cancele o processamento e aguarde sua finalização antes de fechar.", "Processamento em andamento");
            }
        };
        Closed += (_, _) => _updateTimer.Stop();
    }

    private async Task CheckUpdatesAsync()
    {
        if (_checkingUpdates || _downloadingUpdate || _updates is null) return;
        if (!_updates.IsInstalled)
        {
            UpdateStatusText.Text = "Modo portátil/desenvolvimento — atualizações pelo instalador";
            return;
        }
        _checkingUpdates = true;
        try
        {
            _availableUpdate = await _updates.CheckForUpdatesAsync();
            UpdateStatusText.Text = _availableUpdate is null
                ? $"v{_updates.CurrentVersion} — atualizado"
                : $"Versão {_availableUpdate.TargetFullRelease.Version} disponível";
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Não foi possível consultar atualizações. Tente novamente mais tarde.";
            LogUpdateError(ex);
        }
        finally { _checkingUpdates = false; }
    }

    private void LogUpdateError(Exception error)
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            File.AppendAllText(Path.Combine(_logsDirectory, "updates.log"), $"{DateTimeOffset.Now:O} {error}\n");
        }
        catch { }
    }

    private async void Updates_Click(object sender, RoutedEventArgs e)
    {
        UpdatesButton.IsEnabled = false;
        try
        {
            await CheckUpdatesAsync();
            var path = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
            var notes = File.Exists(path) ? File.ReadAllText(path) : "Changelog não incluído nesta compilação.";
            if (_availableUpdate is not null)
                notes = $"NOVA VERSÃO {_availableUpdate.TargetFullRelease.Version}\n\n{_availableUpdate.TargetFullRelease.NotesMarkdown}\n\nINSTALADO\n\n" + notes;
            var dialog = new Window
            {
                Owner = this, Title = "Atualizações e novidades", Width = 760, Height = 570,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = new SolidColorBrush(Color.FromRgb(20, 28, 42))
            };
            var panel = new DockPanel { Margin = new Thickness(16) };
            var action = new Button { Content = "Baixar e instalar atualização", Padding = new Thickness(12),
                IsEnabled = _availableUpdate is not null && _queueCancellation is null && !_downloadingUpdate };
            DockPanel.SetDock(action, Dock.Bottom);
            panel.Children.Add(action);
            var status = new TextBlock { Text = _queueCancellation is null ? UpdateStatusText.Text : "Aguarde o fim da fila ou do piloto para atualizar.",
                Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(status, Dock.Top);
            panel.Children.Add(status);
            panel.Children.Add(new TextBox { Text = notes, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.White, Foreground = Brushes.Black });
            action.Click += async (_, _) =>
            {
                if (_updates is null || _availableUpdate is null || _queueCancellation is not null || _downloadingUpdate) return;
                action.IsEnabled = false;
                _downloadingUpdate = true;
                try
                {
                    var update = _availableUpdate;
                    await _updates.DownloadUpdatesAsync(update, percent => Dispatcher.Invoke(() => status.Text = $"Baixando atualização: {percent}%"));
                    // Recheck after the asynchronous download. Never apply during an encode.
                    if (_queueCancellation is not null)
                    {
                        status.Text = "Download concluído. Aguarde o processamento e abra esta janela novamente.";
                        return;
                    }
                    if (MessageBox.Show(dialog, "Download concluído. Reiniciar para instalar? A lista de arquivos aberta precisará ser adicionada novamente.",
                        "Atualização pronta", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        status.Text = "Atualização baixada. Você pode instalá-la mais tarde.";
                        return;
                    }
                    if (_queueCancellation is not null) return;
                    _applyingUpdate = true;
                    _updates.ApplyUpdatesAndRestart(update);
                }
                catch (Exception ex)
                {
                    _applyingUpdate = false;
                    status.Text = "Falha na atualização. A versão atual foi mantida. Consulte logs/updates.log.";
                    LogUpdateError(ex);
                }
                finally { _downloadingUpdate = false; action.IsEnabled = _queueCancellation is null; }
            };
            dialog.Content = panel;
            dialog.ShowDialog();
        }
        catch (Exception ex) { LogUpdateError(ex); }
        finally { UpdatesButton.IsEnabled = true; }
    }
}
