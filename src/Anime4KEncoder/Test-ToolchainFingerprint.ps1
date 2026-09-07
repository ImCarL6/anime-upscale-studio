param(
    [string]$DllPath = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\Anime4KEncoder.dll')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $DllPath)) { throw "DLL não encontrada: $DllPath" }

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $DllPath).Path)
$builderType = $assembly.GetType('Anime4KEncoder.ToolchainFingerprintBuilder', $true)
$componentType = $assembly.GetType('Anime4KEncoder.ToolchainComponent', $true)
$manifestType = $assembly.GetType('Anime4KEncoder.SegmentManifest', $true)
$pilotResultType = $assembly.GetType('Anime4KEncoder.PilotRunResult', $true)
$buildMethod = $builderType.GetMethod('Build', [Reflection.BindingFlags]'Public,Static')

function New-ComponentArray([object[]]$Values) {
    $array = [Array]::CreateInstance($componentType, $Values.Count)
    for ($index = 0; $index -lt $Values.Count; $index++) {
        $value = $Values[$index]
        $component = [Activator]::CreateInstance($componentType, @([string]$value.Path, [long]$value.Length, [string]$value.Hash))
        $array.SetValue($component, $index)
    }
    return ,$array
}

function Get-Fingerprint([string]$Pipeline, [string]$Environment, [object[]]$Values) {
    $components = New-ComponentArray $Values
    [string]$buildMethod.Invoke($null, @([int]1, [string]'animejanai', $Pipeline, $Environment, $components))
}

$components = @(
    [pscustomobject]@{ Path = 'tools/ffmpeg/ffmpeg.exe'; Length = 100; Hash = ('A' * 64) },
    [pscustomobject]@{ Path = 'models/model.onnx'; Length = 200; Hash = ('B' * 64) }
)
$reversed = @($components[1], $components[0])
$baseline = Get-Fingerprint 'pipeline-v1' 'gpu=4070ti;driver=610.74' $components
$reordered = Get-Fingerprint 'pipeline-v1' 'gpu=4070ti;driver=610.74' $reversed
if ($baseline -ne $reordered) { throw 'A ordem dos componentes alterou o fingerprint canônico.' }
if ($baseline -notmatch '^[0-9A-F]{64}$') { throw "Fingerprint SHA-256 inválido: $baseline" }

$changedComponent = @(
    $components[0],
    [pscustomobject]@{ Path = 'models/model.onnx'; Length = 200; Hash = ('C' * 64) }
)
if ($baseline -eq (Get-Fingerprint 'pipeline-v1' 'gpu=4070ti;driver=610.74' $changedComponent)) {
    throw 'Mudança no conteúdo do modelo não invalidou o fingerprint.'
}
if ($baseline -eq (Get-Fingerprint 'pipeline-v2' 'gpu=4070ti;driver=610.74' $components)) {
    throw 'Mudança no pipeline não invalidou o fingerprint.'
}
if ($baseline -eq (Get-Fingerprint 'pipeline-v1' 'gpu=4070ti;driver=999.99' $components)) {
    throw 'Mudança no driver/GPU não invalidou o fingerprint.'
}
if ($null -eq $manifestType.GetProperty('ToolchainFingerprint')) {
    throw 'O manifesto segmentado não persiste o fingerprint da toolchain.'
}
if ($null -eq $pilotResultType.GetProperty('ToolchainFingerprint')) {
    throw 'O relatório do piloto não persiste o fingerprint da toolchain.'
}

Write-Output 'TOOLCHAIN_FINGERPRINT_VALIDATION=PASS'
Write-Output "BASELINE=$baseline"
Write-Output 'ORDER_INDEPENDENT=TRUE'
Write-Output 'MODEL_PIPELINE_ENVIRONMENT_INVALIDATION=TRUE'
