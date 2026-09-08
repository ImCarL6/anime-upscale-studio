param(
    [string]$DllPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'app\Anime4KEncoder.dll'),
    [Parameter(Mandatory)][string]$InputPath,
    [string]$ProfileName = 'V3 Compact FP16 (Legacy/Experimental)',
    [int]$Cq = 28,
    [int]$TimeoutMinutes = 12
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) {
    throw 'Execute este teste em STA: pwsh -Sta -File .\Test-PilotEndToEnd.ps1'
}
if (-not (Test-Path -LiteralPath $DllPath)) { throw "Aplicativo publicado não encontrado: $DllPath" }
if (-not (Test-Path -LiteralPath $InputPath)) { throw "Entrada de teste não encontrada: $InputPath" }

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore

$projectRoot = Split-Path (Split-Path $DllPath -Parent) -Parent
if (Test-Path (Join-Path (Split-Path $DllPath -Parent) 'tools')) { $projectRoot = Split-Path $DllPath -Parent }
$previousLocation = Get-Location
$previousCurrentDirectory = [Environment]::CurrentDirectory
$previousAutomationValue = $env:ANIME_UPSCALE_STUDIO_AUTOMATION
$env:ANIME_UPSCALE_STUDIO_AUTOMATION = '1'
$previousSynchronizationContext = [Threading.SynchronizationContext]::Current
$window = $null
$timer = $null

try {
    Set-Location -LiteralPath $projectRoot
    [Environment]::CurrentDirectory = $projectRoot
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
    $windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
    $itemType = $assembly.GetType('Anime4KEncoder.EncodeItem', $true)
    $window = [Activator]::CreateInstance($windowType)
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
    $item = [Activator]::CreateInstance($itemType, @((Resolve-Path -LiteralPath $InputPath).Path))
    [void]$window.Items.Add($item)

    $engineCombo = $window.FindName('EngineCombo')
    $profileCombo = $window.FindName('ProfileCombo')
    $queueGrid = $window.FindName('QueueGrid')
    $cqText = $window.FindName('CqText')
    $pilotButton = $window.FindName('PilotButton')
    $cancelButton = $window.FindName('CancelButton')
    $pilotPanel = $window.FindName('PilotSummaryPanel')
    $pilotSummary = $window.FindName('PilotSummaryText')
    $openPilotButton = $window.FindName('OpenPilotButton')

    $engineCombo.SelectedIndex = 1
    $window.Dispatcher.Invoke([Action]{})
    $profileIndex = -1
    for ($index = 0; $index -lt $profileCombo.Items.Count; $index++) {
        if ($profileCombo.Items[$index].Name -eq $ProfileName) { $profileIndex = $index; break }
    }
    if ($profileIndex -lt 0) { throw "Perfil de teste não encontrado: $ProfileName" }
    $profileCombo.SelectedIndex = $profileIndex
    $cqText.Text = [string]$Cq
    $queueGrid.SelectedItem = $item
    $window.Dispatcher.Invoke([Action]{})
    if (-not $pilotButton.IsEnabled) { throw 'Botão de piloto desabilitado antes do teste.' }

    $pilotButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent))
    $window.Dispatcher.Invoke([Action]{})
    if (-not $cancelButton.IsEnabled) { throw 'O piloto não entrou em execução.' }

    $deadline = [DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $script:timedOut = $false
    $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(500), [Windows.Threading.DispatcherPriority]::Background, [EventHandler]{
        if ([DateTimeOffset]::Now -ge $deadline) {
            $script:timedOut = $true
            if ($cancelButton.IsEnabled) { $cancelButton.RaiseEvent([Windows.RoutedEventArgs]::new([Windows.Controls.Button]::ClickEvent)) }
            $frame.Continue = $false
        }
        elseif ($pilotButton.IsEnabled -and -not $cancelButton.IsEnabled) {
            $frame.Continue = $false
        }
    }, $window.Dispatcher)
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)
    $timer.Stop()
    if ($script:timedOut) { throw "O piloto excedeu o limite de $TimeoutMinutes minutos e foi cancelado." }

    $resultField = $windowType.GetField('_lastPilotResult', [Reflection.BindingFlags]'Instance,NonPublic')
    $result = $resultField.GetValue($window)
    if ($null -eq $result) { throw "O piloto terminou sem resultado: $($pilotSummary.Text)" }
    if ($pilotPanel.Visibility -ne [Windows.Visibility]::Visible -or -not $openPilotButton.IsEnabled) {
        throw 'A interface não publicou o resultado e o acesso à amostra validada.'
    }
    if (-not (Test-Path -LiteralPath $result.PreviewPath)) { throw "Amostra não encontrada: $($result.PreviewPath)" }
    if ($null -eq $result.Comparison -or $result.Comparison.Frames.Count -ne $result.SampleCount) { throw 'Comparação automática não foi gerada.' }
    if (!$window.FindName('ComparePilotButton').IsEnabled) { throw 'Botão do comparador não foi habilitado.' }
    foreach ($pair in $result.Comparison.Frames) {
        foreach ($imagePath in @($pair.OriginalPath, $pair.ProcessedPath)) {
            $stream = [IO.File]::OpenRead($imagePath)
            try {
                $decoder = [Windows.Media.Imaging.BitmapDecoder]::Create($stream,[Windows.Media.Imaging.BitmapCreateOptions]::None,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
                if ($decoder.Frames[0].PixelWidth -ne $result.Comparison.Width -or $decoder.Frames[0].PixelHeight -ne $result.Comparison.Height) {throw 'Dimensões de comparação divergentes.'}
            } finally { $stream.Dispose() }
        }
    }
    if ($result.SampleCount -lt 1 -or $result.SampleSeconds -le 0 -or $result.CoreElapsedSeconds -le 0 -or
        $result.VideoBitRate -le 0 -or $result.ProjectedBitRate -le 0 -or $result.ProjectedOutputBytes -le 0 -or $result.ProjectedSeconds -le 0) {
        throw 'O relatório do piloto contém métricas nulas ou inválidas.'
    }
    if ($result.ToolchainFingerprint -notmatch '^[0-9A-F]{64}$') {
        throw "Fingerprint ausente ou inválido no relatório: $($result.ToolchainFingerprint)"
    }
    $snapshotPath = Join-Path $projectRoot "data\toolchain-snapshots\$($result.ToolchainFingerprint).json"
    $fileHashCachePath = Join-Path $projectRoot 'data\toolchain-file-hashes.json'
    if (-not (Test-Path -LiteralPath $snapshotPath) -or -not (Test-Path -LiteralPath $fileHashCachePath)) {
        throw 'O snapshot auditável ou o cache de hashes completos não foi persistido.'
    }

    $firstFingerprint = [string]$result.ToolchainFingerprint
    $window.Close()
    $window = [Activator]::CreateInstance($windowType)
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
    $cachedItem = [Activator]::CreateInstance($itemType, @((Resolve-Path -LiteralPath $InputPath).Path))
    [void]$window.Items.Add($cachedItem)
    $cachedEngineCombo = $window.FindName('EngineCombo')
    $cachedProfileCombo = $window.FindName('ProfileCombo')
    $cachedCqText = $window.FindName('CqText')
    $cachedPanel = $window.FindName('PilotSummaryPanel')
    $cachedEngineCombo.SelectedIndex = 1
    $window.Dispatcher.Invoke([Action]{})
    $cachedProfileIndex = -1
    for ($index = 0; $index -lt $cachedProfileCombo.Items.Count; $index++) {
        if ($cachedProfileCombo.Items[$index].Name -eq $ProfileName) { $cachedProfileIndex = $index; break }
    }
    if ($cachedProfileIndex -lt 0) { throw "Perfil não encontrado ao testar a reativação: $ProfileName" }
    $cachedProfileCombo.SelectedIndex = $cachedProfileIndex
    $cachedCqText.Text = [string]$Cq
    $window.Dispatcher.Invoke([Action]{})

    $cacheDeadline = [DateTimeOffset]::Now.AddSeconds(60)
    $cacheFrame = [Windows.Threading.DispatcherFrame]::new()
    $script:cacheReloaded = $false
    $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(250), [Windows.Threading.DispatcherPriority]::Background, [EventHandler]{
        $cachedResult = $resultField.GetValue($window)
        if ($null -ne $cachedResult -and $cachedResult.ToolchainFingerprint -eq $firstFingerprint) {
            $script:cacheReloaded = $true
            $cacheFrame.Continue = $false
        }
        elseif ([DateTimeOffset]::Now -ge $cacheDeadline) {
            $cacheFrame.Continue = $false
        }
    }, $window.Dispatcher)
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($cacheFrame)
    $timer.Stop()
    if (-not $script:cacheReloaded -or $cachedPanel.Visibility -ne [Windows.Visibility]::Visible) {
        throw 'Uma nova instância não reativou o piloto persistido com a mesma toolchain.'
    }
    if ($cachedItem.EstimatedOutputBytes -ne $result.ProjectedOutputBytes -or $cachedItem.EstimateBasis -notmatch 'piloto medido') {
        throw 'O piloto recuperado não populou a estimativa aproximada do item.'
    }

    Write-Output 'PILOT_END_TO_END=PASS'
    Write-Output 'PILOT_COMPARISON=PASS'
    Write-Output "PROFILE=$($result.ProfileName)"
    Write-Output "SAMPLES=$($result.SampleCount)"
    Write-Output "SAMPLE_SECONDS=$($result.SampleSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))"
    Write-Output "CORE_SECONDS=$($result.CoreElapsedSeconds.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))"
    Write-Output "SPEED=$($result.SpeedFactor.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))x"
    Write-Output "VIDEO_BITRATE=$($result.VideoBitRate)"
    Write-Output "PROJECTED_SIZE=$($result.ProjectedOutputBytes)"
    Write-Output "TOOLCHAIN_FINGERPRINT=$firstFingerprint"
    Write-Output 'PILOT_CACHE_RELOAD=PASS'
    Write-Output 'PILOT_OUTPUT_ESTIMATE_RESTORED=PASS'
    Write-Output "PREVIEW=$($result.PreviewPath)"
    Write-Output "LOG=$($result.LogPath)"
}
finally {
    if ($null -ne $timer) { $timer.Stop() }
    if ($null -ne $window) { $window.Close() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousSynchronizationContext)
    $env:ANIME_UPSCALE_STUDIO_AUTOMATION = $previousAutomationValue
    Set-Location -LiteralPath $previousLocation
    [Environment]::CurrentDirectory = $previousCurrentDirectory
}
