using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Anime4KEncoder;

public partial class PilotComparisonWindow : Window
{
    private readonly PilotComparisonResult _comparison;
    private readonly DispatcherTimer _cycle = new() { Interval = TimeSpan.FromSeconds(3) };

    public PilotComparisonWindow(PilotComparisonResult comparison)
    {
        _comparison = comparison;
        InitializeComponent();
        ProfileText.Text = $"{comparison.ProfileName} • {comparison.Width} × {comparison.Height} • Original ampliado com Lanczos para a mesma resolução";
        ImageCanvas.Width = comparison.Width;
        ImageCanvas.Height = comparison.Height;
        FrameSelector.ItemsSource = comparison.Frames.Select(frame =>
            $"Trecho {frame.SampleIndex + 1} · origem {TimeSpan.FromSeconds(frame.OriginalFrame / comparison.FrameRate):hh\\:mm\\:ss\\.fff}").ToList();
        FrameSelector.SelectedIndex = 0;
        _cycle.Tick += (_, _) => FrameSelector.SelectedIndex = (FrameSelector.SelectedIndex + 1) % comparison.Frames.Count;
        Closed += (_, _) => { _cycle.Stop(); OriginalImage.Source = null; ProcessedImage.Source = null; };
        Loaded += (_, _) => ResizeImages();
    }

    private static BitmapImage ReadImage(string path)
    {
        var image = new BitmapImage();
        using var stream = File.OpenRead(path);
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Frame_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (FrameSelector.SelectedIndex < 0) return;
        try
        {
            var frame = _comparison.Frames[FrameSelector.SelectedIndex];
            OriginalImage.Source = ReadImage(frame.OriginalPath);
            ProcessedImage.Source = ReadImage(frame.ProcessedPath);
            UpdateWipe();
        }
        catch (Exception ex)
        {
            _cycle.Stop();
            OriginalImage.Source = ProcessedImage.Source = null;
            ProfileText.Text = "Imagem indisponível. Execute um novo teste de qualidade. " + ex.Message;
        }
    }

    private void Wipe_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateWipe();
    private void UpdateWipe()
    {
        if (OriginalImage is null || WipeSlider is null) return;
        var split = _comparison.Width * (1 - WipeSlider.Value / 100);
        if (ComparisonStatusText is not null)
            ComparisonStatusText.Text = WipeSlider.Value <= 0
                ? "Você está vendo somente o ORIGINAL, ampliado sem IA."
                : WipeSlider.Value >= 100
                    ? "Você está vendo somente o RESULTADO, processado com IA."
                    : $"Na imagem: ORIGINAL à esquerda ({100 - WipeSlider.Value:0}%) | RESULTADO à direita ({WipeSlider.Value:0}%).";
        Divider.Visibility = WipeSlider.Value is > 0 and < 100 ? Visibility.Visible : Visibility.Hidden;
        OriginalImage.Clip = new RectangleGeometry(new Rect(0, 0, split, _comparison.Height));
        Divider.Margin = new Thickness(Math.Min(split, Math.Max(0, _comparison.Width - 5)), 0, 0, 0);
    }
    private void Zoom_Changed(object sender, SelectionChangedEventArgs e) => ResizeImages();
    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e) => ResizeImages();
    private void ResizeImages()
    {
        if (ImageView is null || Viewport is null || ZoomSelector is null) return;
        var fit = Math.Min(Math.Max(1, Viewport.ActualWidth - 20) / _comparison.Width,
            Math.Max(1, Viewport.ActualHeight - 20) / _comparison.Height);
        var zoom = ZoomSelector.SelectedIndex switch { 1 => 2, 2 => 4, _ => 1 };
        ImageView.Width = _comparison.Width * fit * zoom;
        ImageView.Height = _comparison.Height * fit * zoom;
    }
    private void AutoCycle_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoCycle.IsChecked == true && _comparison.Frames.Count > 1) _cycle.Start();
        else _cycle.Stop();
    }
}
