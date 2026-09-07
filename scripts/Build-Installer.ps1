param(
    [Parameter(Mandatory)][string]$RuntimeSource,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = '',
    [string]$ReleaseNotes = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$RuntimeSource = (Resolve-Path -LiteralPath $RuntimeSource).Path
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\Releases' }
if (!$ReleaseNotes) { $ReleaseNotes = Join-Path $root 'CHANGELOG.md' }
$ReleaseNotes = (Resolve-Path -LiteralPath $ReleaseNotes).Path
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $root ('artifacts\stage-' + $Version + '-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $root 'src\Anime4KEncoder\Anime4KEncoder.csproj'
$config = Join-Path $root 'src\Anime4KEncoder\NuGet.Config'
$vpk = Join-Path $root '.tools\vpk.exe'
if (!(Test-Path $vpk)) {
    dotnet tool install vpk --version 1.2.0 --tool-path (Join-Path $root '.tools') --configfile $config
    if ($LASTEXITCODE) { throw 'Falha ao instalar vpk.' }
}
if (((dotnet tool list --tool-path (Join-Path $root '.tools')) -join "`n") -notmatch '(?m)^vpk\s+1\.2\.0\s') { throw 'Use vpk 1.2.0, igual ao SDK Velopack do aplicativo.' }
$manifestPath = Join-Path $PSScriptRoot 'runtime-files.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $source = Join-Path $RuntimeSource $entry.Path
    if (!(Test-Path -LiteralPath $source) -or (Get-FileHash -LiteralPath $source).Hash -ne $entry.SHA256) {
        throw "Dependência ausente ou diferente da versão validada: $($entry.Path). Veja docs/DEPENDENCIES.md."
    }
}
dotnet restore $project -r win-x64 --locked-mode --configfile $config
if ($LASTEXITCODE) { throw 'Falha ao restaurar pacotes.' }
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -p:Version=$Version -o $stage
if ($LASTEXITCODE) { throw 'Falha na compilação.' }
foreach ($entry in $manifest) {
    $destination = Join-Path $stage $entry.Path
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $RuntimeSource $entry.Path) -Destination $destination
}
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'runtime-manifest.json')
Copy-Item -LiteralPath (Join-Path $root 'shaders') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $stage
& $vpk pack --packId ImCarL6.AnimeUpscaleStudio --packVersion $Version --packDir $stage --mainExe Anime4KEncoder.exe --packTitle 'Anime Upscale Studio' --packAuthors ImCarL6 --icon (Join-Path $root 'src\Anime4KEncoder\painter.ico') --releaseNotes $ReleaseNotes --channel win --outputDir $OutputDirectory
if ($LASTEXITCODE) { throw 'Falha no empacotamento Velopack.' }
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    "$((Get-FileHash -LiteralPath $_.FullName).Hash)  $($_.Name)"
} | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8
Write-Output "INSTALLER_BUILD=PASS: $OutputDirectory"
