param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
# Run against an isolated build directory: this writes a synthetic queue and history.
if (Test-Path -LiteralPath (Join-Path $Package 'sq.version')) { throw 'Use an isolated portable test directory.' }
if (Test-Path -LiteralPath (Join-Path $Package 'data')) { throw 'Test directory already contains user data.' }
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $Package
[Environment]::CurrentDirectory = $Package
New-Item -ItemType Directory -Force 'shaders','tools/ffmpeg' | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Package 'Anime4KEncoder.dll'))
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
$itemType = $assembly.GetType('Anime4KEncoder.EncodeItem', $true)
$mediaType = $assembly.GetType('Anime4KEncoder.MediaInfo', $true)
$recordType = $assembly.GetType('Anime4KEncoder.HistoryRecord', $true)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
function Check([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
$window = [Activator]::CreateInstance($windowType)
try {
    $mediaJson = @{ Duration='00:20:00'; Size=1000000000L; BitRate=6666667L; Width=1920; Height=1080;
        VideoCodec='h264'; PixelFormat='yuv420p'; HasAssSubtitle=$false; AudioCodecs=@('aac','aac') } | ConvertTo-Json
    $media = [Text.Json.JsonSerializer]::Deserialize($mediaJson, $mediaType)
    $one = [Activator]::CreateInstance($itemType, @('first.mkv'))
    $two = [Activator]::CreateInstance($itemType, @('second.mkv'))
    foreach ($item in @($one, $two)) { $item.ApplyMediaInfo($media); $item.Status='Aguardando'; $window.Items.Add($item) }
    $refresh = $windowType.GetMethod('UpdateEstimates', $flags)
    $refresh.Invoke($window, @())
    $initialBytes = $one.EstimatedOutputBytes
    $initialSeconds = $one.EstimatedTotalSeconds
    $initialBasis = $one.EstimateBasis
    $events = [Collections.Generic.List[string]]::new()
    $two.add_PropertyChanged([ComponentModel.PropertyChangedEventHandler]{ param($sender,$eventArgs) $events.Add($eventArgs.PropertyName) })
    $beta = $windowType.GetMethod('ReadBetaOptions', $flags).Invoke($window, @())
    $recordJson = @{ Timestamp=[DateTimeOffset]::Now; Engine=$window.FindName('EngineCombo').SelectedItem.Code;
        Profile=$window.FindName('ProfileCombo').SelectedItem.Code; Cq=[int]$window.FindName('CqText').Text;
        InputBytes=$media.Size; OutputBytes=$initialBytes*2; DurationSeconds=1200; ElapsedSeconds=$initialSeconds*2;
        InputWidth=1920; InputHeight=1080; InputBitRate=6666667; AudioTracks=2; InputVideoCodec='h264';
        InputPixelFormat='yuv420p'; Concurrency=1; IsBeta=$beta.AnyEnabled; BetaSignature=$beta.Signature;
        PipelineVersion=1; ResourcePercent=$windowType.GetMethod('ReadResourcePercent',$flags).Invoke($window,@()) } | ConvertTo-Json
    $record = [Text.Json.JsonSerializer]::Deserialize($recordJson, $recordType)
    $windowType.GetMethod('RecordHistory',$flags).Invoke($window, @($record))
    $one.ActualOutputBytes=$record.OutputBytes
    $one.Status='Concluído'
    $one.Progress=100
    $refresh.Invoke($window, @())
    Check ($two.EstimatedOutputBytes -gt $initialBytes -and $two.EstimatedTotalSeconds -gt $initialSeconds) 'Next episode did not learn from completion.'
    Check ($events.Contains('EtaText') -and $events.Contains('TimeText') -and $events.Contains('SizeText')) 'Visible row did not receive estimate updates.'
    Check ($one.EstimatedOutputBytes -eq $initialBytes -and $one.EstimatedTotalSeconds -eq $initialSeconds -and $one.EstimateBasis -eq $initialBasis) 'Completed prediction changed retrospectively.'
    Check ($one.SizeComparisonText -match '100[,.]0%') 'Completed comparison no longer shows the original prediction error.'
    $expectedTotal=$one.ActualOutputBytes+$two.EstimatedOutputBytes
    $formatter=$windowType.GetMethod('FormatBytes',[Reflection.BindingFlags]'Static,NonPublic')
    $formattedTotal=$formatter.Invoke($null,@([long]$expectedTotal))
    Check ($window.FindName('EstimateSummaryText').Text.Contains($formattedTotal)) 'Total did not combine actual completed bytes with pending estimates.'
    $savedTimeText=$two.TimeText
    $windowType.GetMethod('SaveSession',$flags).Invoke($window,@())
    $window.Close()
    $window=[Activator]::CreateInstance($windowType)
    Check ($window.Items[0].EstimatedOutputBytes -eq $initialBytes -and $window.Items[0].EstimatedTotalSeconds -eq $initialSeconds) 'Reopening lost the original completed prediction.'
    Check ($window.Items[1].TimeText -eq $savedTimeText) 'Reopening lost learned estimates.'
    Write-Output 'ADAPTIVE_QUEUE_UI=PASS: completed measurement, pending row/ETA notifications, original comparison, actual totals, session/history reload.'
} finally { $window.Close() }
