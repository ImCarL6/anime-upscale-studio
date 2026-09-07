param(
    [Parameter(Mandatory)][string]$QueuePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [switch]$PreflightOnly
)
$ErrorActionPreference = 'Stop'
$studioRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
Set-Location -LiteralPath $studioRoot
[Environment]::CurrentDirectory = $studioRoot
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$runLog = Join-Path $ReportDirectory 'recovery.log'
$statusPath = Join-Path $ReportDirectory 'status.json'
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $studioRoot 'app\Anime4KEncoder.dll'))
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
$window = [Activator]::CreateInstance($windowType)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,NonPublic'
$cancellation = [Threading.CancellationTokenSource]::new()
$windowType.GetField('_queueCancellation',$instanceFlags).SetValue($window,$cancellation)
$window.Add_Closing({ $cancellation.Cancel() })
$script:jobs = [Collections.Generic.List[object]]::new()
$script:queueState = 'preparing'
function Write-RecoveryLog([string]$Message) {
    [IO.File]::AppendAllText($runLog, "[$([DateTimeOffset]::Now.ToString('O'))] $Message`n",[Text.UTF8Encoding]::new($false))
}
function Write-Status {
    $state = [ordered]@{UpdatedAt=[DateTimeOffset]::Now;ProcessId=$PID;State=$script:queueState;Jobs=@($script:jobs | ForEach-Object {
        [ordered]@{Input=$_.Item.InputPath;Profile=$_.Manifest.ProfileCode;Cq=$_.Manifest.Cq;Status=$_.Item.Status;Stage=$_.Item.Stage;Progress=$_.Item.Progress;Output=$_.Item.OutputPath;Log=$_.Item.LogPath;Error=$_.Item.Error;Manifest=$_.ManifestPath}
    })}
    [IO.File]::WriteAllText($statusPath,($state | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
function Await-RecoveryTask([Threading.Tasks.Task]$Task) {
    if(-not $Task.IsCompleted){
        $frame=[Windows.Threading.DispatcherFrame]::new()
        $timer=[Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(250),[Windows.Threading.DispatcherPriority]::Background,[EventHandler]{if($Task.IsCompleted){$frame.Continue=$false}},$window.Dispatcher)
        $timer.Start()
        try {[Windows.Threading.Dispatcher]::PushFrame($frame)} finally {$timer.Stop()}
    }
    $result=$Task.GetAwaiter().GetResult()
    return ,$result
}
function Invoke-Studio([string]$Name,[object[]]$Arguments,[switch]$Static) {
    $method=$windowType.GetMethod($Name,$(if($Static){$staticFlags}else{$instanceFlags}))
    if($null -eq $method){throw "Método não encontrado: $Name"}
    $result=$method.Invoke($(if($Static){$null}else{$window}),$Arguments)
    if($result -is [Threading.Tasks.Task]){return ,(Await-RecoveryTask $result)}
    return ,$result
}
function Set-JobControls($Job) {
    $window.FindName('EngineCombo').SelectedIndex=1
    $profiles=$window.FindName('ProfileCombo')
    for($index=0;$index -lt $profiles.Items.Count;$index++){if($profiles.Items[$index].Code -eq $Job.Manifest.ProfileCode){$profiles.SelectedIndex=$index;break}}
    $window.FindName('CqText').Text=[string]$Job.Manifest.Cq
    $window.FindName('OutputDirectoryText').Text=$Job.Settings.OutputDirectory
    $window.FindName('QueueGrid').SelectedItem=$Job.Item
    [void](Invoke-Studio 'SetRunningUi' ([object[]]@($true)))
}
try {
    $manifestPaths=@(Get-Content -LiteralPath $QueuePath -Raw -Encoding UTF8 | ConvertFrom-Json)
    foreach($manifestPath in $manifestPaths){
        $manifestPath=[IO.Path]::GetFullPath([string]$manifestPath)
        $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if($manifest.IsBeta -or $manifest.Version -ne 1 -or $manifest.SegmentSeconds -ne 300 -or $manifest.OverlapFrames -ne 2){throw 'Manifesto fora do fluxo estável suportado.'}
        $sourceFile=Get-Item -LiteralPath $manifest.SourcePath
        if($sourceFile.Length -ne $manifest.SourceLength -or $sourceFile.LastWriteTimeUtc.Ticks -ne $manifest.SourceLastWriteUtcTicks){throw "A origem mudou: $($manifest.SourcePath)"}
        $cacheDirectory=Split-Path -Parent $manifestPath
        $resumeDirectory=Split-Path -Parent $cacheDirectory
        $outputDirectory=Split-Path -Parent (Split-Path -Parent $resumeDirectory)
        $item=[Activator]::CreateInstance($assembly.GetType('Anime4KEncoder.EncodeItem'),[object[]]@([string]$manifest.SourcePath))
        $media=Invoke-Studio 'ProbeMediaAsync' ([object[]]@([string]$item.InputPath,$cancellation.Token))
        $item.ApplyMediaInfo($media)
        $item.Status='Aguardando'
        $item.Stage='Na fila de recuperação'
        $beta=$assembly.GetType('Anime4KEncoder.BetaFfmpegOptions').GetConstructors()[0].Invoke([object[]]@($false,$false,$false,$false,$false))
        $settings=$assembly.GetType('Anime4KEncoder.JobSettings').GetConstructors()[0].Invoke([object[]]@(
            [string]'animejanai',[string]$manifest.ProfileCode,[string]$manifest.ProfileCode,[int]$manifest.Cq,[int]1,[string]$outputDirectory,[string]'',[string]$manifest.ModelName,[string]'',$false,$beta))
        $expectedResume=Invoke-Studio 'BuildSegmentedWorkDirectory' ([object[]]@([string]$outputDirectory,$item,$settings))
        if(-not [string]::Equals($expectedResume,$resumeDirectory,[StringComparison]::OrdinalIgnoreCase)){throw "O diretório recuperável não corresponde à configuração: $resumeDirectory"}
        $snapshotPath=Join-Path $studioRoot ('data\toolchain-snapshots\'+$manifest.ToolchainFingerprint+'.json')
        if(-not(Test-Path -LiteralPath $snapshotPath)){throw "Snapshot original ausente: $snapshotPath"}
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $ReportDirectory ($manifest.ProfileCode+'-'+$sourceFile.BaseName+'-manifest-before.json'))
        $job=[pscustomobject]@{Manifest=$manifest;ManifestPath=$manifestPath;CacheDirectory=$cacheDirectory;ResumeDirectory=$resumeDirectory;Item=$item;Settings=$settings}
        $script:jobs.Add($job)
        $window.Items.Add($item)
    }
    if($PreflightOnly){
        foreach($job in $script:jobs){
            $allComplete=@($job.Manifest.Segments | Where-Object {-not $_.Completed}).Count -eq 0
            if(-not $allComplete){
                $snapshot=Invoke-Studio 'ComputeToolchainSnapshotAsync' ([object[]]@($job.Settings,$cancellation.Token))
                if($snapshot.Hash -ne $job.Manifest.ToolchainFingerprint){throw "Fingerprint atual difere para $($job.Item.FileName)"}
            }
        }
        $script:queueState='preflight_passed'
        Write-Status
        'RECOVERY_PREFLIGHT=PASS'
        $window.Close()
        exit 0
    }
    $window.Title='Anime Upscale Studio — retomada dos episódios'
    $window.Show()
    $windowType.GetField('_queueStartedAt',$instanceFlags).SetValue($window,[DateTimeOffset]::Now)
    $elapsed=$windowType.GetField('_elapsedTimer',$instanceFlags).GetValue($window)
    $elapsed.Start()
    $statusTimer=[Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromSeconds(3),[Windows.Threading.DispatcherPriority]::Background,[EventHandler]{Write-Status},$window.Dispatcher)
    $statusTimer.Start()
    $script:queueState='running'
    Write-RecoveryLog 'Fila iniciada; configurações originais e caches preservados.'
    foreach($job in $script:jobs){
        if($cancellation.IsCancellationRequested){break}
        $item=$job.Item
        Set-JobControls $job
        Write-RecoveryLog ("Iniciando: "+$item.FileName)
        try {
            # Preserve interrupted artifacts before the normal pipeline retries their blocks.
            foreach($partial in @(Get-ChildItem -LiteralPath $job.CacheDirectory -File | Where-Object {$_.Name.EndsWith('.partial.mkv')})){
                $backupDirectory=Join-Path $job.CacheDirectory 'interrupted-before-recovery'
                New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
                $saved=Join-Path $backupDirectory ($partial.Name+'.'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.saved')
                Copy-Item -LiteralPath $partial.FullName -Destination $saved
            }
            $allComplete=@($job.Manifest.Segments | Where-Object {-not $_.Completed}).Count -eq 0
            if($allComplete){
                $source=Invoke-Studio 'ProbeMediaStructureAsync' ([object[]]@([string]$item.InputPath,$true,$cancellation.Token))
                if($source.VideoPackets -ne $job.Manifest.TotalFrames -or [Math]::Abs($source.AverageFrameRate-$job.Manifest.FrameRate) -gt 0.00001){throw 'O inventário da origem difere do manifesto.'}
                $item.StartedAt=[DateTimeOffset]::Now
                $item.IsRunning=$true
                $item.Status='Verificando'
                $item.Stage='Validando blocos concluídos'
                $item.LogPath=Join-Path $ReportDirectory ($job.Manifest.ProfileCode+'-'+[IO.Path]::GetFileNameWithoutExtension($item.InputPath)+'-remux.log')
                foreach($checkpoint in $job.Manifest.Segments){
                    $segmentPath=Join-Path $job.CacheDirectory ('segment-{0:0000}.mkv' -f ($checkpoint.Index+1))
                    if((Get-Item -LiteralPath $segmentPath).Length -ne $checkpoint.OutputBytes){throw "Tamanho do bloco mudou: $segmentPath"}
                    [void](Invoke-Studio 'ValidateVideoSegmentAsync' ([object[]]@([string]$segmentPath,[long]($checkpoint.UsefulEndFrame-$checkpoint.UsefulStartFrame),$cancellation.Token,[int]($source.Width*2),[int]($source.Height*2))))
                    Write-RecoveryLog ("Bloco recuperado e validado: "+$segmentPath)
                }
                $plans=Invoke-Studio 'BuildSegmentPlan' ([object[]]@([long]$source.VideoPackets,[long][Math]::Round($source.AverageFrameRate*300,[MidpointRounding]::AwayFromZero),[int]2)) -Static
                $concatPath=Join-Path $job.CacheDirectory 'segments.ffconcat'
                [void](Invoke-Studio 'WriteConcatManifest' ([object[]]@([string]$concatPath,$plans)) -Static)
                $baseName=Invoke-Studio 'SafeBaseName' ([object[]]@([string][IO.Path]::GetFileNameWithoutExtension($item.InputPath),[int]160)) -Static
                # Match the application's normal output name without altering the source.
                $finalPath=Join-Path $job.Settings.OutputDirectory ($baseName+' [AnimeJaNai '+$job.Manifest.ProfileCode+' AV1 CQ'+$job.Manifest.Cq+'].mkv')
                if(Test-Path -LiteralPath $finalPath){throw "Saída já existe: $finalPath"}
                $partialPath=$finalPath+'.recovery-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.partial.mkv'
                $muxArguments=Invoke-Studio 'BuildSegmentedFinalMuxArguments' ([object[]]@([string]$concatPath,[string]$item.InputPath,[string]$partialPath,$source)) -Static
                [void](Invoke-Studio 'RunFfmpegAsync' ([object[]]@($item,$muxArguments,[double]94,[double]5.5,[string]$item.LogPath,$cancellation.Token,[TimeSpan]$item.Duration,[string]'Montagem dos blocos recuperados')))
                $item.Stage='Validando arquivo completo'
                $compatibility=Invoke-Studio 'ValidateSegmentedOutputAsync' ([object[]]@($item,[string]$partialPath,$source,$cancellation.Token))
                [IO.File]::Move($partialPath,$finalPath)
                $item.OutputPath=$finalPath
                $item.ActualOutputBytes=(Get-Item -LiteralPath $finalPath).Length
                $item.Compatibility=$compatibility
                $item.Progress=100
                $item.Processed=$item.Duration
                $item.Status='Concluído'
                $item.Stage='Finalizado'
            }else{
                $snapshot=Invoke-Studio 'ComputeToolchainSnapshotAsync' ([object[]]@($job.Settings,$cancellation.Token))
                if($snapshot.Hash -ne $job.Manifest.ToolchainFingerprint){throw "A toolchain atual difere da original; cache preservado. Original=$($job.Manifest.ToolchainFingerprint); atual=$($snapshot.Hash)"}
                [void](Invoke-Studio 'EncodeAsync' ([object[]]@($item,$job.Settings,$cancellation.Token)))
            }
            Write-RecoveryLog ("Resultado: "+$item.FileName+' — '+$item.Status+' — '+$item.Error)
        }catch{
            $item.Status=if($cancellation.IsCancellationRequested){'Cancelado'}else{'Falhou'}
            $item.Error=$_.Exception.ToString()
            Write-RecoveryLog $item.Error
        }finally{
            $item.IsRunning=$false
            $item.FinishTiming([DateTimeOffset]::Now)
            $item.UpdateProgress()
            Write-Status
        }
    }
    $script:queueState=if($cancellation.IsCancellationRequested){'cancelled'}elseif(@($script:jobs|Where-Object {$_.Item.Status -eq 'Falhou'}).Count){'finished_with_errors'}else{'completed'}
    $elapsed.Stop()
    [void](Invoke-Studio 'SetRunningUi' ([object[]]@($false)))
    $windowType.GetField('_queueCancellation',$instanceFlags).SetValue($window,$null)
    Write-Status
    Write-RecoveryLog ('Fila: '+$script:queueState)
    $statusTimer.Stop()
    if($window.IsVisible){[Windows.Threading.Dispatcher]::Run()}
}catch{
    $script:queueState='failed'
    Write-RecoveryLog $_.Exception.ToString()
    Write-Status
    throw
}
