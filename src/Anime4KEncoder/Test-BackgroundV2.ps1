param([Parameter(Mandatory)][string]$Package, [string]$FixturePath = '', [switch]$ForceRecovery, [switch]$CancelRecovery, [string]$ProfileCode = 'V3.1-Balanced', [switch]$LiveSwitch)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $Package
[Environment]::CurrentDirectory = $Package
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Package 'Anime4KEncoder.dll'))
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
$itemType = $assembly.GetType('Anime4KEncoder.EncodeItem', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$window = [Activator]::CreateInstance($windowType)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
function Check([bool]$Condition, [string]$Message) { if (!$Condition) {throw $Message} }
function Pump-Until([scriptblock]$Condition, [int]$TimeoutSeconds = 120) {
    $deadline = [DateTimeOffset]::Now.AddSeconds($TimeoutSeconds)
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(100),[Windows.Threading.DispatcherPriority]::Background,[EventHandler]{
        if ((& $Condition) -or [DateTimeOffset]::Now -ge $deadline) {$frame.Continue = $false}
    },$window.Dispatcher)
    $timer.Start()
    try {[Windows.Threading.Dispatcher]::PushFrame($frame)} finally {$timer.Stop()}
    Check (& $Condition) 'Test timed out.'
}
try {
    $mode=$window.FindName('ResourceModeCombo')
    Check ($mode.SelectedIndex -eq 1) 'Default background mode wrong.'
    Check ($window.FindName('CqText').Text -eq '18') 'CQ default wrong.'
    Check ($window.FindName('ProfileCombo').SelectedItem.Code -eq 'V3.1-Balanced-Sharp1') 'Profile default wrong.'
    $mode.SelectedIndex=0
    $profiles=$window.FindName('ProfileCombo')
    for($i=0;$i -lt $profiles.Items.Count;$i++) {if($profiles.Items[$i].Code -eq $ProfileCode) {$profiles.SelectedIndex=$i;break}}
    $item=[Activator]::CreateInstance($itemType,@([IO.Path]::GetFullPath($FixturePath)))
    $probe=$windowType.GetMethod('ProbeMediaAsync',$flags).Invoke($window,@($FixturePath,[Threading.CancellationToken]::None))
    Pump-Until {$probe.IsCompleted} 30
    $item.ApplyMediaInfo($probe.GetAwaiter().GetResult())
    $item.Status='Aguardando'
    $window.Items.Add($item)
    $output=Join-Path $Package 'validation\background-v2\output'
    if($ForceRecovery) {$output=Join-Path $Package ('validation\background-v2\recovery-'+[guid]::NewGuid().ToString('N'))}
    if($LiveSwitch) {$output=Join-Path $Package ('validation\live\switch-'+[guid]::NewGuid().ToString('N'))}
    $window.FindName('OutputDirectoryText').Text=$output
    $parameters=[object[]]@($null)
    Check ($windowType.GetMethod('TryBuildCurrentFingerprintSettings',$flags).Invoke($window,$parameters)) 'Could not build settings.'
    $cts=[Threading.CancellationTokenSource]::new()
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$cts)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $task=$windowType.GetMethod('EncodeInBackgroundAsync',$flags).Invoke($window,@($item,$parameters[0],$cts.Token))
    if($LiveSwitch) {
        $windowType.GetMethod('SetRunningUi',$flags).Invoke($window,@($true))
        Check $mode.IsEnabled 'Live mode selector is disabled while running.'
        Pump-Until { $live=$windowType.GetField('_livePacing',$flags).GetValue($window); $null -ne $live -and $live.GetType().GetProperty('AppliedRate',$flags).GetValue($live) -eq 0.15 } 120
        $pacing=$windowType.GetField('_livePacing',$flags).GetValue($window)
        $originalLog=$item.LogPath
        foreach($index in @(1,0,2)) {
            $clockSwitch=[Diagnostics.Stopwatch]::StartNew()
            $mode.SelectedIndex=$index
            $expected=@(0.15,0.25,1000000)[$index]
            Pump-Until { $pacing.GetType().GetProperty('AppliedRate',$flags).GetValue($pacing) -eq $expected } 5
            Check ([object]::ReferenceEquals($pacing,$windowType.GetField('_livePacing',$flags).GetValue($window))) 'Pacing process was replaced.'
            Check (!$cts.IsCancellationRequested) 'Live switch cancelled queue.'
            Check ($originalLog -eq $item.LogPath) 'Live switch restarted the attempt.'
            Write-Output ('LIVE_UI_SWITCH='+$index+'; ACK_MS='+$clockSwitch.ElapsedMilliseconds)
        }
    }
    if($ForceRecovery) {
        Pump-Until { $null -ne $windowType.GetField('_resourceProtection',$flags).GetValue($window) -and $item.IsRunning -and (Test-Path -LiteralPath $item.LogPath) -and ((Get-Content -LiteralPath $item.LogPath -Raw).Contains('FFmpeg AV1 bloco')) } 60
        $guard=$windowType.GetField('_resourceProtection',$flags).GetValue($window)
        $guard.GetType().GetMethod('Stop',$flags).Invoke($guard,@('Pressão simulada pelo teste; nenhum esgotamento real.'))
        Pump-Until { $item.Stage -like 'Aguardando retomada*' } 30
        if($CancelRecovery) {
            $cts.Cancel()
            Pump-Until {$task.IsCompleted} 15
            Check $task.IsCanceled 'User cancellation did not end automatic recovery.'
            Check ($null -eq $windowType.GetField('_resourceProtection',$flags).GetValue($window)) 'Guard remains after cancellation.'
            Write-Output 'CANCEL_DURING_RECOVERY=PASS: queue cancelled, no restart, workers disposed.'
            return
        }
    }
    Pump-Until {$task.IsCompleted} 180
    [void]$task.GetAwaiter().GetResult()
    Check ($item.Status -eq 'Concluído') ('Encode failed: '+$item.Error+'; log='+$item.LogPath)
    Check (Test-Path -LiteralPath $item.OutputPath) 'Output missing.'
    Check (!(Test-Path -LiteralPath (Join-Path $output '.anime-upscale-temp'))) 'Successful work temp remains.'
    $log=Get-Content -LiteralPath $item.LogPath -Raw
    if(!$ForceRecovery) { Check ($log.Contains('realtime@throttle')) 'Live pacing missing in real command.' }
    if($ForceRecovery) {
        Check ($log.Contains('speed=0.075')) 'Retry did not slow down.'
        Check ($clock.Elapsed.TotalSeconds -ge 30) 'Recovery cooldown not applied.'
        Write-Output 'AUTOMATIC_RECOVERY=PASS: forced attempt stop, cooldown, slower retry, final validation and cleanup.'
    }
    Write-Output ('BACKGROUND_PIPELINE=PASS: '+$item.OutputPath+'; wall='+$clock.Elapsed.TotalSeconds+'; log='+$item.LogPath)
    if($LiveSwitch) {
        Check (!$log.Contains('Retomada automática')) 'Live mode changes restarted processing.'
        Check ($windowType.GetField('_resourceModeChangedDuringRun',$flags).GetValue($window)) 'Mixed-mode history marker missing.'
        Write-Output 'LIVE_UI_PIPELINE=PASS: modes changed within active block, same attempt, final validation and cleanup.'
    }
    $cts.Dispose()
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$null)
} finally { $active=$windowType.GetField('_queueCancellation',$flags).GetValue($window); if($active){$active.Cancel()}; $windowType.GetField('_queueCancellation',$flags).SetValue($window,$null); $window.Close() }
