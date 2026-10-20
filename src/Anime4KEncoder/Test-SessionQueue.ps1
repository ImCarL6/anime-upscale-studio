param([Parameter(Mandatory)][string]$Package, [Parameter(Mandatory)][string]$FixturePath)
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
    $window.Items.Clear()
    $mode=$window.FindName('ResourceModeCombo')
    Check ($mode.SelectedIndex -eq 1) 'Default mode must be balanced.'
    Check ($window.FindName('ProfileCombo').SelectedItem.Code -eq 'V3.1-Balanced-Sharp1') 'Default profile wrong.'
    Check ($window.FindName('CqText').Text -eq '18') 'CQ default wrong.'
    $runRoot=Join-Path $Package ('validation\session-check-'+[guid]::NewGuid().ToString('N'))
    $inputs=Join-Path $runRoot 'inputs'
    $output=Join-Path $runRoot 'output'
    New-Item -ItemType Directory -Force $inputs,$output | Out-Null
    $one=Join-Path $inputs 'first.mkv'
    $two=Join-Path $inputs 'second.mkv'
    $bad=Join-Path $inputs 'truncated.mkv'
    Copy-Item -LiteralPath $FixturePath -Destination $one -Force
    Copy-Item -LiteralPath $FixturePath -Destination $two -Force
    $bytes=[IO.File]::ReadAllBytes($FixturePath)
    [IO.File]::WriteAllBytes($bad,$bytes[0..([int]($bytes.Length*0.65))])
    $window.FindName('OutputDirectoryText').Text=$output
    $addArgs=[object[]]::new(1); $addArgs[0]=[string[]]@($one)
    $add=$windowType.GetMethod('AddInputsAsync',$flags).Invoke($window,$addArgs)
    Pump-Until {$add.IsCompleted} 30
    [void]$add.GetAwaiter().GetResult()
    $parameters=[object[]]@($null)
    Check ($windowType.GetMethod('TryBuildCurrentFingerprintSettings',$flags).Invoke($window,$parameters)) 'Settings invalid.'
    $cts=[Threading.CancellationTokenSource]::new()
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$cts)
    $watch=$windowType.GetField('_queueWatch',$flags).GetValue($window)
    $windowType.GetField('_savedQueueSeconds',$flags).SetValue($window,[double]12)
    $watch.Start()
    $windowType.GetMethod('SetRunningUi',$flags).Invoke($window,@($true))
    $task=$windowType.GetMethod('RunQueueAsync',$flags).Invoke($window,@($parameters[0],$cts.Token))
    Pump-Until {$window.Items[0].IsRunning} 30
    $addArgs=[object[]]::new(1); $addArgs[0]=[string[]]@($two,$bad)
    $add=$windowType.GetMethod('AddInputsAsync',$flags).Invoke($window,$addArgs)
    Pump-Until {$add.IsCompleted} 30
    [void]$add.GetAwaiter().GetResult()
    Check ($window.Items.Count -eq 3) 'Adding during run lost files.'
    Check ($window.FindName('ResourceModeCombo').IsEnabled) 'Live resource mode locked.'
    Pump-Until {$task.IsCompleted} 240
    [void]$task.GetAwaiter().GetResult()
    $watch.Stop()
    Check ($window.Items[0].Status -eq 'Concluído') ('First failed: '+$window.Items[0].Error)
    Check ($window.Items[1].Status -eq 'Concluído') ('Dynamically added second failed: '+$window.Items[1].Error)
    Check ($window.Items[2].Status -eq 'Falhou') 'Truncated input accepted.'
    Check ($window.Items[2].Error -match 'incompleto|prematuro') ('Truncation diagnostic unclear: '+$window.Items[2].Error)
    $badlog=Get-Content -LiteralPath $window.Items[2].LogPath -Raw
    Check (!$badlog.Contains('AnimeJaNai segmentado recuperável')) 'Truncation was detected after upscale, not before.'
    Check (!$badlog.Contains('Retomada automática')) 'Corrupt input entered resource-retry loop.'
    $windowType.GetMethod('SaveSession',$flags).Invoke($window,@())
    $savedPath=Join-Path $Package 'data\queue-session.json'
    $saved=Get-Content -LiteralPath $savedPath -Raw | ConvertFrom-Json
    Check ($saved.Items.Count -eq 3 -and $saved.ElapsedSeconds -gt 12) 'Queue/time were not saved.'
    $elapsed=$saved.ElapsedSeconds
    $cts.Dispose()
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$null)
    $window.Close()
    $window=[Activator]::CreateInstance($windowType)
    Check ($window.Items.Count -eq 3) 'Restored queue count wrong.'
    Check ($window.Items[0].Status -eq 'Concluído' -and $window.Items[1].Status -eq 'Concluído') 'Completed items would run again.'
    Check ($window.Items[2].Status -eq 'Falhou') 'Failed item state lost.'
    Check ($window.FindName('OutputDirectoryText').Text -eq $output) 'Output folder lost.'
    Check ($windowType.GetField('_inputDirectory',$flags).GetValue($window) -eq $inputs) 'Input picker folder lost.'
    Check ([Math]::Abs($windowType.GetField('_savedQueueSeconds',$flags).GetValue($window)-$elapsed) -lt 0.1) 'Accumulated queue time lost.'
    Check ($null -eq $windowType.GetField('_queueCancellation',$flags).GetValue($window)) 'Restore started processing automatically.'
    # Simulate a saved in-flight item; on the next construction it must become pending.
    $window.Items[2].Status='Processando'
    $windowType.GetMethod('SaveSession',$flags).Invoke($window,@())
    $window.Close()
    $window=[Activator]::CreateInstance($windowType)
    Check ($window.Items[2].Status -eq 'Aguardando') 'Interrupted work was not made pending on restart.'
    Write-Output 'SESSION_AND_DYNAMIC_QUEUE=PASS: add during run, two final outputs, corrupt input rejected before upscale without retry, order/statuses/folders/time restored, in-flight restored pending, no auto-start.'
    # A subtitle extending after the A/V end must not be rejected as truncated.
    $srt=Join-Path $inputs 'extended.srt'
    [IO.File]::WriteAllText($srt,"1`n00:00:03,000 --> 00:00:07,000`nExtended subtitle`n")
    $extended=Join-Path $inputs 'extended.mkv'
    & (Join-Path $Package 'tools\ffmpeg\ffmpeg.exe') -hide_banner -nostdin -y -v error -i $one -i $srt -map 0:v:0 -map '0:a?' -map 1:s:0 -c copy $extended
    Check ($LASTEXITCODE -eq 0) 'Subtitle fixture mux failed.'
    $item=$itemType.GetConstructor([type[]]@([string])).Invoke([object[]]@([string]$extended))
    $item.LogPath=Join-Path $inputs 'extended-validation.log'
    $checkTask=$windowType.GetMethod('ValidateInputIntegrityAsync',$flags).Invoke($window,@($item,[Threading.CancellationToken]::None))
    Pump-Until {$checkTask.IsCompleted} 30
    [void]$checkTask.GetAwaiter().GetResult()
    Write-Output 'INPUT_EXTENDED_SUBTITLES=PASS: actual MKV with subtitles ending after A/V accepted.'
} finally {
    $active=$windowType.GetField('_queueCancellation',$flags).GetValue($window)
    if($active){$active.Cancel()}
    $windowType.GetField('_queueCancellation',$flags).SetValue($window,$null)
    $window.Close()
}