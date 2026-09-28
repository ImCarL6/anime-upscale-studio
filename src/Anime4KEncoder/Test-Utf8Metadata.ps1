param(
    [string]$DllPath = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\Anime4KEncoder.dll'),
    [string]$RuntimeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
)
$ErrorActionPreference = 'Stop'
$studioRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$work = Join-Path $studioRoot ('validation\utf8-metadata-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $work -Force | Out-Null
$ffmpeg = Join-Path $RuntimeRoot 'tools\ffmpeg\ffmpeg.exe'
$source = Join-Path $work 'source.mkv'
$subtitle = Join-Path $work 'subtitle.srt'
[IO.File]::WriteAllText($subtitle, "1`n00:00:00,000 --> 00:00:00,800`nOlá`n", [Text.UTF8Encoding]::new($false))
& $ffmpeg -hide_banner -nostdin -v error -f lavfi -i 'color=c=blue:s=64x64:r=1:d=1' -i $subtitle -map 0:v -map 1:s -c:v libx264 -c:s srt -metadata:s:s:0 'language=por' -metadata:s:s:0 'title=Português (Brasil) - Yami Studio' -disposition:s:0 default $source
if ($LASTEXITCODE) { throw 'Falha na criação da mídia de teste.' }
Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Set-Location -LiteralPath $RuntimeRoot
[Environment]::CurrentDirectory = $RuntimeRoot
$assembly = [Reflection.Assembly]::LoadFrom($DllPath)
$windowType = $assembly.GetType('Anime4KEncoder.MainWindow', $true)
$window = [Activator]::CreateInstance($windowType)
try {
    $flags = [Reflection.BindingFlags]'Instance,NonPublic'
    $method = $windowType.GetMethod('ProbeMediaStructureAsync', $flags)
    $task = $method.Invoke($window, [object[]]@([string]$source, $false, [Threading.CancellationToken]::None))
    $media = $task.GetAwaiter().GetResult()
    $track = @($media.PreservedStreams | Where-Object Type -eq 'subtitle')[0]
    if ($track.Title -cne 'Português (Brasil) - Yami Studio') { throw "Título alterado: $($track.Title)" }
    if ($track.Language -ne 'por' -or $track.Disposition -ne 'default') { throw 'Idioma ou disposição alterados.' }
    'UTF8_METADATA=PASS'
    "WORK=$work"
} finally { $window.Close() }
