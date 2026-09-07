param(
    [string]$DllPath = (Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'app\Anime4KEncoder.dll'),
    [Parameter(Mandatory)][string]$InputPath,
    [string]$ModelName = '2x_AnimeJaNai_HD_V3_Compact_strong_fp16',
    [int]$TimeoutMinutes = 12
)

$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.ApartmentState -ne [Threading.ApartmentState]::STA) { throw 'Execute este teste em STA.' }
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore

$projectRoot = Split-Path (Split-Path $DllPath -Parent) -Parent
$work = Join-Path $projectRoot ('validation\fingerprint-segmented-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$log = Join-Path $work 'segmented-fingerprint.log'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$previousLocation = Get-Location
$previousContext = [Threading.SynchronizationContext]::Current
$window = $null

function Wait-DispatcherTask([Threading.Tasks.Task]$Task, [Windows.Threading.Dispatcher]$Dispatcher, [DateTimeOffset]$Deadline) {
    if (-not $Task.IsCompleted) {
        $frame = [Windows.Threading.DispatcherFrame]::new()
        $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(200), [Windows.Threading.DispatcherPriority]::Background, [EventHandler]{
            if ($Task.IsCompleted -or [DateTimeOffset]::Now -ge $Deadline) { $frame.Continue = $false }
        }, $Dispatcher)
        $timer.Start()
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        $timer.Stop()
    }
    if (-not $Task.IsCompleted) { throw 'A operação segmentada excedeu o limite de tempo.' }
    $Task.GetAwaiter().GetResult()
}

try {
    Set-Location -LiteralPath $projectRoot
    $assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
    $windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
    $itemType = $assembly.GetType('Anime4KEncoder.EncodeItem', $true)
    $jobType = $assembly.GetType('Anime4KEncoder.JobSettings', $true)
    $betaType = $assembly.GetType('Anime4KEncoder.BetaFfmpegOptions', $true)
    $window = [Activator]::CreateInstance($windowType)
    [Threading.SynchronizationContext]::SetSynchronizationContext(
        [Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
    $item = [Activator]::CreateInstance($itemType, @((Resolve-Path -LiteralPath $InputPath).Path))
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $probeMethod = $windowType.GetMethod('ProbeMediaAsync', $flags)
    $probeTask = [Threading.Tasks.Task]$probeMethod.Invoke($window, [object[]]@($item.InputPath, [Threading.CancellationToken]::None))
    $media = Wait-DispatcherTask $probeTask $window.Dispatcher ([DateTimeOffset]::Now.AddMinutes(2))
    $item.ApplyMediaInfo($media)

    $beta = $betaType.GetConstructors()[0].Invoke([object[]]@($false,$false,$false,$false,$false))
    $jobConstructor = $jobType.GetConstructors()[0]
    function New-Settings([int]$Cq) {
        $jobConstructor.Invoke([object[]]@(
            [string]'animejanai',[string]'V3-Compact',[string]'V3 Compact FP16 (Legacy/Experimental)',
            [int]$Cq,[int]1,[string]$work,[string]'',[string]$ModelName,[string]'',[bool]$false,$beta))
    }
    $runMethod = $windowType.GetMethod('RunAnimeJanaiSegmentedAsync', $flags)
    $deadline = [DateTimeOffset]::Now.AddMinutes($TimeoutMinutes)

    $settings28 = New-Settings 28
    $firstTask = [Threading.Tasks.Task]$runMethod.Invoke($window, [object[]]@(
        $item,$settings28,[string](Join-Path $work 'first.mkv'),[string]$work,[string]$log,[Threading.CancellationToken]::None))
    [void](Wait-DispatcherTask $firstTask $window.Dispatcher $deadline)
    $firstCaches = @(Get-ChildItem -LiteralPath $work -Directory -Filter 'toolchain-*')
    if ($firstCaches.Count -ne 1) { throw "Era esperado um namespace de toolchain; encontrados $($firstCaches.Count)." }
    $manifestPath = Join-Path $firstCaches[0].FullName 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.ToolchainFingerprint -notmatch '^[0-9A-F]{64}$') { throw 'Manifesto sem fingerprint SHA-256 completo.' }
    $segmentPath = Join-Path $firstCaches[0].FullName 'segment-0001.mkv'
    $segmentHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $segmentPath).Hash
    $segmentWriteBefore = (Get-Item -LiteralPath $segmentPath).LastWriteTimeUtc

    $secondTask = [Threading.Tasks.Task]$runMethod.Invoke($window, [object[]]@(
        $item,$settings28,[string](Join-Path $work 'second.mkv'),[string]$work,[string]$log,[Threading.CancellationToken]::None))
    [void](Wait-DispatcherTask $secondTask $window.Dispatcher $deadline)
    $segmentHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $segmentPath).Hash
    $segmentWriteAfter = (Get-Item -LiteralPath $segmentPath).LastWriteTimeUtc
    if ($segmentHashAfter -ne $segmentHashBefore -or $segmentWriteAfter -ne $segmentWriteBefore) {
        throw 'O segundo processamento refez ou alterou um segmento compatível.'
    }

    $settings29 = New-Settings 29
    $thirdTask = [Threading.Tasks.Task]$runMethod.Invoke($window, [object[]]@(
        $item,$settings29,[string](Join-Path $work 'cq29.mkv'),[string]$work,[string]$log,[Threading.CancellationToken]::None))
    [void](Wait-DispatcherTask $thirdTask $window.Dispatcher $deadline)
    $allCaches = @(Get-ChildItem -LiteralPath $work -Directory -Filter 'toolchain-*')
    if ($allCaches.Count -ne 2) { throw 'Uma configuração materialmente diferente não recebeu namespace de cache isolado.' }
    $fingerprints = @($allCaches | ForEach-Object {
        (Get-Content -LiteralPath (Join-Path $_.FullName 'manifest.json') -Raw | ConvertFrom-Json).ToolchainFingerprint
    } | Select-Object -Unique)
    if ($fingerprints.Count -ne 2) { throw 'Os namespaces diferentes persistiram o mesmo fingerprint.' }

    Write-Output 'SEGMENTED_FINGERPRINT_CACHE=PASS'
    Write-Output "REUSED_SEGMENT_SHA256=$segmentHashAfter"
    Write-Output "CACHE_NAMESPACES=$($allCaches.Name -join ' | ')"
    Write-Output "WORK=$work"
}
finally {
    if ($null -ne $window) { $window.Close() }
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
    Set-Location -LiteralPath $previousLocation
}
