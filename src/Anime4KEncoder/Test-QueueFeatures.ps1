param([Parameter(Mandatory)][string]$Package, [string]$PilotReport = '', [string]$RenderPath = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $Package
[Environment]::CurrentDirectory = $Package
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Package 'Anime4KEncoder.dll'))
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow',$true)
$itemType = $assembly.GetType('Anime4KEncoder.EncodeItem',$true)
$window = [Activator]::CreateInstance($windowType)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
try {
    $episode2 = [Activator]::CreateInstance($itemType,@('episode-02.mkv'))
    $episode1 = [Activator]::CreateInstance($itemType,@('episode-01.mkv'))
    $window.Items.Add($episode2)
    $window.Items.Add($episode1)
    $move = $windowType.GetMethod('MoveQueueItem',$flags)
    $move.Invoke($window,@($episode1,0))
    if ($window.Items[0] -ne $episode1 -or $window.Items[1] -ne $episode2) {throw 'Reorder did not change the actual queue.'}
    $cancellation = [Threading.CancellationTokenSource]::new()
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$cancellation)
    $move.Invoke($window,@($episode2,0))
    if ($window.Items[0] -ne $episode1) {throw 'Active queue was reordered.'}
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$null)
    $cancellation.Dispose()
    if ($episode1.CanOpenOutput) {throw 'Incomplete episode exposes output actions.'}
    $episode1.OutputPath = 'result.mkv'
    $episode1.Status = 'Concluído'
    if (!$episode1.CanOpenOutput) {throw 'Completed episode did not enable output actions.'}
    $episode1.IsRunning = $true
    if ($episode1.CanOpenOutput) {throw 'Running episode exposes output actions.'}
    $episode1.IsRunning = $false
    $episode1.Status = 'Falhou'
    if ($episode1.CanOpenOutput) {throw 'Failed episode exposes output actions.'}
    if ($window.FindName('QueueGrid').CanUserSortColumns) {throw 'Visual sorting could diverge from queue order.'}
    Write-Output 'QUEUE_FEATURES=PASS: actual reorder, active queue lock, output actions gated by success'
    if ($PilotReport) {
        $reportType = $assembly.GetType('Anime4KEncoder.PilotRunResult',$true)
        $result = [Text.Json.JsonSerializer]::Deserialize([IO.File]::ReadAllText($PilotReport),$reportType)
        if (!$result.Comparison) {throw 'Missing comparison.'}
        $comparisonType = $assembly.GetType('Anime4KEncoder.PilotComparisonWindow',$true)
        $comparison = [Activator]::CreateInstance($comparisonType,@($result.Comparison))
        try {
            $slider = $comparison.FindName('WipeSlider')
            $original = $comparison.FindName('OriginalImage')
            $slider.Value = 0
            if ($original.Clip.Rect.Width -ne 0) {throw 'Processed-only view has original pixels.'}
            $slider.Value = 100
            if ($original.Clip.Rect.Width -ne $result.Comparison.Width) {throw 'Original-only view is incomplete.'}
            $slider.Value = 50
            $comparison.FindName('FrameSelector').SelectedIndex = $result.Comparison.Frames.Count - 1
            if (!$original.Source -or !$comparison.FindName('ProcessedImage').Source) {throw 'Frame switch failed.'}
            if ($RenderPath) {
                $comparison.WindowStartupLocation = [Windows.WindowStartupLocation]::Manual
                $comparison.Left = -10000
                $comparison.Top = -10000
                $comparison.ShowInTaskbar = $false
                $comparison.ShowActivated = $false
                $comparison.Show()
                $content = $comparison.Content
                $content.UpdateLayout()
                $content.Dispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::Render)
                $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($content.ActualWidth),[int][Math]::Ceiling($content.ActualHeight),96,96,[Windows.Media.PixelFormats]::Pbgra32)
                $bitmap.Render($content)
                $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
                $stream = [IO.File]::Create($RenderPath)
                try {$encoder.Save($stream)} finally {$stream.Dispose()}
            }
            Write-Output 'COMPARISON_VIEWER=PASS: exact wipe endpoints, frame switching, persisted report'
        } finally {$comparison.Close()}
    }
} finally {$window.Close()}
