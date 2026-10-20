param([Parameter(Mandatory)][string]$Package,[switch]$CancelAndResume)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $Package
[Environment]::CurrentDirectory=$Package
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $Package 'Anime4KEncoder.dll'))
$type=$assembly.GetType('Anime4KEncoder.MainWindow',$true)
$itemType=$assembly.GetType('Anime4KEncoder.EncodeItem',$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$window=[Activator]::CreateInstance($type)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
function Check([bool]$ok,[string]$message){if(!$ok){throw $message}}
function Pump([scriptblock]$condition,[int]$seconds=180){
 $deadline=[DateTimeOffset]::Now.AddSeconds($seconds)
 $frame=[Windows.Threading.DispatcherFrame]::new()
 $timer=[Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(100),[Windows.Threading.DispatcherPriority]::Background,[EventHandler]{
  if((& $condition) -or [DateTimeOffset]::Now -ge $deadline){$frame.Continue=$false}
 },$window.Dispatcher)
 $timer.Start();try{[Windows.Threading.Dispatcher]::PushFrame($frame)}finally{$timer.Stop()}
 Check (& $condition) 'Dynamic integration timed out.'
}
try{
 $work=Join-Path $Package ('validation\dynamic-'+[guid]::NewGuid().ToString('N'))
 New-Item -ItemType Directory -Path $work | Out-Null
 $source=Join-Path $work 'source.mkv'
 & (Join-Path $Package 'tools\ffmpeg\ffmpeg.exe') -hide_banner -v error -nostdin -y -f lavfi -i 'testsrc2=size=1280x720:rate=1:duration=480' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=480' -map 0:v -map 1:a -c:v ffv1 -level 3 -c:a aac -shortest $source
 Check ($LASTEXITCODE -eq 0) 'Synthetic input generation failed.'
 $mode=$window.FindName('ResourceModeCombo');$mode.SelectedIndex=2
 $window.Items.Clear()
 $window.FindName('OutputDirectoryText').Text=Join-Path $work 'output'
 $item=$itemType.GetConstructor([type[]]@([string])).Invoke([object[]]@([string]$source))
 $probe=$type.GetMethod('ProbeMediaAsync',$flags).Invoke($window,[object[]]@([string]$source,[Threading.CancellationToken]::None))
 Pump {$probe.IsCompleted} 30
 $item.ApplyMediaInfo($probe.GetAwaiter().GetResult());$item.Status='Aguardando';$window.Items.Add($item)
 $parameters=[object[]]@($null)
 Check ($type.GetMethod('TryBuildCurrentFingerprintSettings',$flags).Invoke($window,$parameters)) 'No settings.'
 $cts=[Threading.CancellationTokenSource]::new();$type.GetField('_queueCancellation',$flags).SetValue($window,$cts)
 $task=$type.GetMethod('EncodeInBackgroundAsync',$flags).Invoke($window,@($item,$parameters[0],$cts.Token))
 $script:phase=0;$script:failure=$null;$script:cache=''
 $frame=[Windows.Threading.DispatcherFrame]::new();$deadline=[DateTimeOffset]::Now.AddMinutes(6)
 $timer=[Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(100),[Windows.Threading.DispatcherPriority]::Background,[EventHandler]{
  try{
   if(!$script:cache -and $item.LogPath -and (Test-Path -LiteralPath $item.LogPath)){
    $match=[regex]::Match([IO.File]::ReadAllText($item.LogPath),'(?m)^Cache=(.+)$')
    if($match.Success){$script:cache=$match.Groups[1].Value.Trim()}
   }
   if($script:cache -and (Test-Path -LiteralPath (Join-Path $script:cache 'manifest.json'))){
    $state=Get-Content -LiteralPath (Join-Path $script:cache 'manifest.json') -Raw | ConvertFrom-Json
    if($script:phase -eq 0 -and $state.Segments[0].Completed){
     Check ($state.Segments[0].UsefulEndFrame -eq 300) 'Maximum block was not five minutes.'
     $mode.SelectedIndex=0;$script:phase=1
    }elseif($script:phase -eq 1 -and $state.Segments.Count -ge 2){
     Check ($state.Segments[1].UsefulStartFrame -eq 300 -and $state.Segments[1].UsefulEndFrame -eq 360) 'Economical block was not one minute.'
     # The selected boundary is now fixed; free the pace while this block encodes.
     $mode.SelectedIndex=2;$script:phase=2
    }elseif($script:phase -eq 2 -and $state.Segments[1].Completed){
     $mode.SelectedIndex=1;$script:phase=3
     if($CancelAndResume){
      $script:retained=@(Get-ChildItem -LiteralPath $script:cache -Filter 'segment-*.mkv' -File | ForEach-Object {[pscustomobject]@{Path=$_.FullName;Hash=(Get-FileHash -LiteralPath $_.FullName).Hash;WriteTicks=$_.LastWriteTimeUtc.Ticks}})
      $cts.Cancel()
     }
    }elseif($script:phase -eq 3 -and $state.Segments.Count -ge 3){
     Check ($state.Segments[2].UsefulStartFrame -eq 360 -and $state.Segments[2].UsefulEndFrame -eq 480) 'Balanced final block did not end exactly at the input end.'
     if($CancelAndResume){
      Check ($script:retained.Count -eq 2) 'Expected two retained blocks.'
      foreach($file in $script:retained){Check ((Get-FileHash -LiteralPath $file.Path).Hash -eq $file.Hash -and (Get-Item -LiteralPath $file.Path).LastWriteTimeUtc.Ticks -eq $file.WriteTicks) 'Completed block was rewritten during resume.'}
      $script:resumeVerified=$true
     }
     $state | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $work 'mixed-plan.json')
     $mode.SelectedIndex=2;$script:phase=4
    }
   }
  }catch{$script:failure=$_;$cts.Cancel()}
  if($task.IsCompleted -or $script:failure -or [DateTimeOffset]::Now -ge $deadline){$frame.Continue=$false}
 },$window.Dispatcher)
 $timer.Start();try{[Windows.Threading.Dispatcher]::PushFrame($frame)}finally{$timer.Stop()}
 if($script:failure){throw $script:failure}
 if($CancelAndResume){
  Check ($task.IsCanceled -and $item.Status -eq 'Cancelado' -and $script:phase -eq 3) 'First pass did not preserve cancellation.'
  Check (Test-Path -LiteralPath $script:cache) 'Cancellation removed the cache.'
  # A cancelled encoder may leave an unfinished partial; this must not be adopted or treated as an orphan completed block.
  [IO.File]::WriteAllText((Join-Path $script:cache 'segment-0003.mkv.partial.mkv'),'unfinished synthetic partial')
  $cts.Dispose();$cts=[Threading.CancellationTokenSource]::new();$type.GetField('_queueCancellation',$flags).SetValue($window,$cts)
  $task=$type.GetMethod('EncodeInBackgroundAsync',$flags).Invoke($window,@($item,$parameters[0],$cts.Token))
  $frame=[Windows.Threading.DispatcherFrame]::new();$deadline=[DateTimeOffset]::Now.AddMinutes(6)
  $timer.Start();try{[Windows.Threading.Dispatcher]::PushFrame($frame)}finally{$timer.Stop()}
  if($script:failure){throw $script:failure}
  Check ($script:resumeVerified -eq $true) 'Resume did not verify the retained block identities.'
  Write-Output 'DYNAMIC_RESUME=PASS: cancel preserved two differently sized blocks; resumed with new mode without rewriting either completed block.'
 }
 Check $task.IsCompleted 'Pipeline timed out.'
 [void]$task.GetAwaiter().GetResult()
 Check ($item.Status -eq 'Concluído') ('Output failed: '+$item.Error)
 Check ($script:phase -eq 4) 'Boundary mode changes were not all observed.'
 Check (Test-Path -LiteralPath $item.OutputPath) 'Final output missing.'
 Check (!(Test-Path -LiteralPath $script:cache)) 'Successful job cache was not cleaned.'
 Write-Output ('DYNAMIC_PIPELINE=PASS: real 8-minute CFR fixture / 480 frames; 5min -> 1min -> balanced final tail; same queue, exact final frame/timestamp/audio validation and cleanup; '+$item.OutputPath)
}finally{
 if($cts){$cts.Cancel()}
 $type.GetField('_queueCancellation',$flags).SetValue($window,$null)
 $window.Close()
}



