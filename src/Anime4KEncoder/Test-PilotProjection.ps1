param(
    [string]$DllPath = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Anime4KEncoder.dll')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $DllPath)) { throw "DLL não encontrada: $DllPath" }

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
$planner = $assembly.GetType('Anime4KEncoder.PilotPlanner', $true)

$episodeFrames = [long]33100
$fps = 24000.0 / 1001.0
$samples = @($planner::BuildSamples($episodeFrames, $fps))
if ($samples.Count -ne 3) { throw "Episódio deveria gerar três amostras; gerou $($samples.Count)." }
if (@($samples | Where-Object FrameCount -ne 120).Count -ne 0) { throw 'As amostras do episódio não têm cinco segundos arredondados por frames.' }
for ($index = 1; $index -lt $samples.Count; $index++) {
    if ($samples[$index].StartFrame -lt ($samples[$index - 1].StartFrame + $samples[$index - 1].FrameCount)) {
        throw 'As amostras representativas se sobrepõem.'
    }
}
$centers = @($samples | ForEach-Object { ($_.StartFrame + $_.FrameCount / 2.0) / $episodeFrames })
$expectedCenters = @(0.18, 0.50, 0.82)
for ($index = 0; $index -lt 3; $index++) {
    if ([Math]::Abs($centers[$index] - $expectedCenters[$index]) -gt 0.002) {
        throw "Centro inesperado para amostra $($index + 1): $($centers[$index])"
    }
}

$tenSecondSamples = @($planner::BuildSamples(240, 24.0))
if ($tenSecondSamples.Count -ne 2 -or $tenSecondSamples[0].StartFrame -ne 0 -or $tenSecondSamples[1].StartFrame -ne 120) {
    throw 'Vídeo de dez segundos não foi dividido em duas amostras exatas e não sobrepostas.'
}
$shortSamples = @($planner::BuildSamples(72, 24.0))
if ($shortSamples.Count -ne 1 -or $shortSamples[0].StartFrame -ne 0 -or $shortSamples[0].FrameCount -ne 72) {
    throw 'Vídeo curto não foi limitado a uma única amostra integral.'
}

$projection = $planner::CalculateProjection('animejanai', 15.0, 45.0, 30000000, 1380.0, 384000)
if ([Math]::Abs($projection.SpeedFactor - (1.0 / 3.0)) -gt 0.000001) { throw 'Coeficiente de velocidade incorreto.' }
if ($projection.VideoBitRate -ne 16000000) { throw "Bitrate de vídeo incorreto: $($projection.VideoBitRate)" }
if ([Math]::Abs($projection.ProjectedSeconds - 4471.2) -gt 0.001) { throw "Tempo projetado incorreto: $($projection.ProjectedSeconds)" }
if ([Math]::Abs($projection.MinimumProjectedSeconds / $projection.ProjectedSeconds - 0.85) -gt 0.000001 -or
    [Math]::Abs($projection.MaximumProjectedSeconds / $projection.ProjectedSeconds - 1.15) -gt 0.000001) {
    throw 'Margem de projeção incorreta.'
}
if ($projection.ProjectedBitRate -le $projection.VideoBitRate -or $projection.ProjectedOutputBytes -le 0) {
    throw 'Áudio, overhead ou tamanho final não foram incorporados à projeção.'
}

Write-Output 'PILOT_PLANNER_VALIDATION=PASS'
Write-Output "SAMPLES=$($samples.Count)x$($samples[0].FrameCount)_FRAMES"
Write-Output "SPEED=$($projection.SpeedFactor.ToString('0.###', [Globalization.CultureInfo]::InvariantCulture))x"
Write-Output "PROJECTED_SECONDS=$($projection.ProjectedSeconds.ToString('0.0', [Globalization.CultureInfo]::InvariantCulture))"
