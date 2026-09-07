param(
    [string]$DllPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'app\Anime4KEncoder.dll'),
    [Parameter(Mandatory)][string]$InputPath,
    [string]$ProfileName = 'V3 Compact FP16 (Legacy/Experimental)',
    [int]$Cq = 28,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) { throw 'Execute este teste em STA.' }
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore

$projectRoot = Split-Path (Split-Path $DllPath -Parent) -Parent
$previousLocation = Get-Location
$previousContext = [Threading.SynchronizationContext]::Current
$window = $null
$timer = $null
try {
    Set-Location -LiteralPath $projectRoot
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
    $windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
    $itemType = $assembly.GetType('Anime4KEncoder.EncodeItem', $true)
    $window = [Activator]::CreateInstance($windowType)
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
    $item = [Activator]::CreateInstance($itemType, @((Resolve-Path -LiteralPath $InputPath).Path))
    [void]$window.Items.Add($item)
    $engine = $window.FindName('EngineCombo')
    $profile = $window.FindName('ProfileCombo')
    $cqText = $window.FindName('CqText')
    $panel = $window.FindName('PilotSummaryPanel')
    $summary = $window.FindName('PilotSummaryText')
    $engine.SelectedIndex = 1
    $window.Dispatcher.Invoke([Action]{})
    $profileIndex = -1
    for ($index = 0; $index -lt $profile.Items.Count; $index++) {
        if ($profile.Items[$index].Name -eq $ProfileName) { $profileIndex = $index; break }
    }
    if ($profileIndex -lt 0) { throw "Perfil não encontrado: $ProfileName" }
    $profile.SelectedIndex = $profileIndex
    $cqText.Text = [string]$Cq
    $window.Dispatcher.Invoke([Action]{})

    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $resultField = $windowType.GetField('_lastPilotResult', $flags)
    $activeField = $windowType.GetField('_activeToolchainFingerprint', $flags)
    $historyField = $windowType.GetField('_pilotHistory', $flags)
    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $script:cacheReloaded = $false
    $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(250), [Windows.Threading.DispatcherPriority]::Background, [EventHandler]{
        if ($null -ne $resultField.GetValue($window) -and $panel.Visibility -eq [Windows.Visibility]::Visible) {
            $script:cacheReloaded = $true
            $frame.Continue = $false
        }
        elseif ([DateTimeOffset]::Now -ge $deadline) {
            $frame.Continue = $false
        }
    }, $window.Dispatcher)
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)
    $timer.Stop()

    $result = $resultField.GetValue($window)
    $active = [string]$activeField.GetValue($window)
    $history = $historyField.GetValue($window)
    Write-Output "CACHE_HISTORY_COUNT=$($history.Count)"
    Write-Output "ACTIVE_FINGERPRINT=$active"
    Write-Output "RESULT_FINGERPRINT=$(if ($null -ne $result) { $result.ToolchainFingerprint } else { '' })"
    Write-Output "PANEL=$($panel.Visibility)"
    Write-Output "SUMMARY=$($summary.Text)"
    if (-not $script:cacheReloaded) { throw 'O piloto persistido não foi reativado.' }
    Write-Output 'PILOT_CACHE_RELOAD=PASS'
}
finally {
    if ($null -ne $timer) { $timer.Stop() }
    if ($null -ne $window) { $window.Close() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
    Set-Location -LiteralPath $previousLocation
}
