param([string]$DllPath = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Anime4KEncoder.dll'))
$ErrorActionPreference = 'Stop'
$studioRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$work = Join-Path $studioRoot ('validation\resume-validation-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $work | Out-Null
$ffmpeg = Join-Path $studioRoot 'tools\ffmpeg\ffmpeg.exe'
$source = Join-Path $work 'source-und-720p.mp4'
$output = Join-Path $work 'output-und-1440p.mkv'
$segment = Join-Path $work 'segment-1440p.mkv'
& $ffmpeg -hide_banner -nostdin -v error -f lavfi -i 'color=c=blue:s=1280x720:r=24:d=1' -f lavfi -i 'sine=frequency=440:sample_rate=48000:duration=1' -map 0:v -map 1:a -c:v libx264 -preset ultrafast -bf 0 -pix_fmt yuv420p -c:a aac -metadata:s:v:0 language=und -metadata:s:a:0 language=por $source
if ($LASTEXITCODE) { throw 'Falha na criação da origem de teste.' }
& $ffmpeg -hide_banner -nostdin -v error -i $source -map 0:v -map 0:a -vf scale=2560:1440 -c:v av1_nvenc -preset p1 -cq 40 -pix_fmt p010le -c:a aac -q:a 2 -ar:a 48000 -af:a 'aresample=async=1:first_pts=0' -metadata:s:v:0 language=und -metadata:s:a:0 language=por -avoid_negative_ts make_zero $output
if ($LASTEXITCODE) { throw 'Falha na criação da saída de teste.' }
& $ffmpeg -hide_banner -nostdin -v error -i $output -map 0:v -c copy $segment
if ($LASTEXITCODE) { throw 'Falha na criação do bloco de teste.' }
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $studioRoot
[Environment]::CurrentDirectory = $studioRoot
$assembly = [Reflection.Assembly]::LoadFrom($DllPath)
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
$window = [Activator]::CreateInstance($windowType)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
$instance = [Reflection.BindingFlags]'Instance,NonPublic'
$static = [Reflection.BindingFlags]'Static,NonPublic'
$none = [Threading.CancellationToken]::None
function Await-Task([Threading.Tasks.Task]$Task) {
    if (-not $Task.IsCompleted) {
        $frame = [Windows.Threading.DispatcherFrame]::new()
        $timer = [Windows.Threading.DispatcherTimer]::new([TimeSpan]::FromMilliseconds(50), [Windows.Threading.DispatcherPriority]::Background, [EventHandler]{ if($Task.IsCompleted){$frame.Continue=$false} }, $window.Dispatcher)
        $timer.Start()
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        $timer.Stop()
    }
    $Task.GetAwaiter().GetResult()
}
try {
    $probe = $windowType.GetMethod('ProbeMediaStructureAsync', $instance)
    $src = Await-Task ($probe.Invoke($window, [object[]]@([string]$source,$true,$none)))
    $dst = Await-Task ($probe.Invoke($window, [object[]]@([string]$output,$true,$none)))
    if (-not $src.VideoIdentity.Equals($dst.VideoIdentity)) {throw 'und e idioma ausente continuam distintos.'}
    $builder = $windowType.GetMethod('BuildStreamIdentity', $static)
    $recognized = [Text.Json.JsonDocument]::Parse('{"tags":{"language":"jpn"},"disposition":{"default":1}}')
    $jpn = $builder.Invoke($null,[object[]]@($recognized.RootElement,'video'))
    if ($jpn.Equals($dst.VideoIdentity)) {throw 'A validação perdeu a distinção de um idioma identificado.'}
    $validateSegment = $windowType.GetMethod('ValidateVideoSegmentAsync',$instance)
    [void](Await-Task ($validateSegment.Invoke($window,[object[]]@([string]$segment,[long]24,$none,[int]2560,[int]1440))))
    $rejected = $false
    try {[void](Await-Task ($validateSegment.Invoke($window,[object[]]@([string]$segment,[long]24,$none,[int]3840,[int]2160))))} catch {if($_.Exception.ToString() -match 'resolução inesperada'){$rejected=$true}else{throw}}
    if(-not $rejected){throw 'Uma resolução incorreta foi aceita.'}
    $item = [Activator]::CreateInstance($assembly.GetType('Anime4KEncoder.EncodeItem'),[object[]]@([string]$source))
    $media = Await-Task ($windowType.GetMethod('ProbeMediaAsync',$instance).Invoke($window,[object[]]@([string]$source,$none)))
    $item.ApplyMediaInfo($media)
    $item.LogPath = Join-Path $work 'validation.log'
    [void](Await-Task ($windowType.GetMethod('ValidateSegmentedOutputAsync',$instance).Invoke($window,[object[]]@($item,[string]$output,$src,$none))))
    'RESUME_VALIDATION=PASS: und/empty, known language preservation, 720p-to-1440p, wrong resolution rejection, full frame/timeline/audio validation.'
    "WORK=$work"
} finally {$window.Close()}
