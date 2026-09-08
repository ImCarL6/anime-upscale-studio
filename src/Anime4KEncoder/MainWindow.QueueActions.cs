using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Anime4KEncoder;

public partial class MainWindow
{
    private const string QueueDragFormat = "AnimeUpscaleStudio.QueueItem";
    private Point _queueDragStart;
    private EncodeItem? _queueDragItem;

    private void QueueHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _queueDragItem = _queueCancellation is null ? (sender as FrameworkElement)?.DataContext as EncodeItem : null;
        _queueDragStart = e.GetPosition(QueueGrid);
    }

    private void QueueHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _queueCancellation is not null) { _queueDragItem = null; return; }
        if (_queueDragItem is null) return;
        var position = e.GetPosition(QueueGrid);
        if (Math.Abs(position.X - _queueDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _queueDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _queueDragItem;
        _queueDragItem = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(QueueDragFormat, item), DragDropEffects.Move);
    }

    private void QueueGrid_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _queueCancellation is null && e.Data.GetData(QueueDragFormat) is EncodeItem item && Items.Contains(item)
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void QueueGrid_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_queueCancellation is not null || e.Data.GetData(QueueDragFormat) is not EncodeItem item) return;
        var row = ItemsControl.ContainerFromElement(QueueGrid, e.OriginalSource as DependencyObject) as DataGridRow;
        var target = row?.Item as EncodeItem;
        var position = target is null ? Items.Count - 1 : Items.IndexOf(target);
        MoveQueueItem(item, position);
    }

    private void MoveQueueItem(EncodeItem item, int position)
    {
        var oldPosition = Items.IndexOf(item);
        if (_queueCancellation is not null || oldPosition < 0 || position < 0 || position >= Items.Count || oldPosition == position) return;
        Items.Move(oldPosition, position);
        QueueGrid.SelectedItem = item;
        QueueGrid.ScrollIntoView(item);
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e) => OpenCompletedOutput(sender, false);
    private void OpenOutputFolder_Click(object sender, RoutedEventArgs e) => OpenCompletedOutput(sender, true);

    private void OpenCompletedOutput(object sender, bool folder)
    {
        if ((sender as FrameworkElement)?.DataContext is not EncodeItem item || !item.CanOpenOutput) return;
        if (!File.Exists(item.OutputPath))
        {
            MessageBox.Show(this, "O arquivo final foi movido ou removido. Verifique a pasta de saída.", "Arquivo não encontrado");
            return;
        }
        try
        {
            Process.Start(folder
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{item.OutputPath}\"") { UseShellExecute = true }
                : new ProcessStartInfo(item.OutputPath) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "Não foi possível abrir o resultado.\n" + ex.Message, "Abrir resultado"); }
    }

    private async Task PrepareTensorRtAsync(EncodeItem item, string input, string config, double start, double span, string log, CancellationToken cancellationToken)
    {
        var marker = Path.Combine(_dataRoot, "data", "tensorrt-introduction-seen.txt");
        await Dispatcher.InvokeAsync(() =>
        {
            TensorRtNoticeText.Text = File.Exists(marker)
                ? "Verificando a engine existente. Se o modelo, a resolução ou o runtime mudou, uma nova preparação pode levar vários minutos. Aguarde; você pode cancelar."
                : "No primeiro uso, o TensorRT precisa preparar o modelo para sua GPU e resolução. Isso pode levar vários minutos sem avanço na porcentagem. O aplicativo está trabalhando: aguarde ou use Cancelar. A engine será reutilizada nas próximas execuções compatíveis.";
            TensorRtNotice.Visibility = Visibility.Visible;
            item.Stage = "Preparando TensorRT — aguarde, isso pode levar vários minutos";
        });
        try
        {
            await RunAjiAsync(item, BuildAnimeJanaiEngineArguments(input, config), start, span, log, cancellationToken);
            try { Directory.CreateDirectory(Path.GetDirectoryName(marker)!); File.WriteAllText(marker, "1"); }
            catch (IOException) { /* A notice preference must not invalidate a successful engine build. */ }
            catch (UnauthorizedAccessException) { }
        }
        finally { await Dispatcher.InvokeAsync(() => TensorRtNotice.Visibility = Visibility.Collapsed); }
    }
}
