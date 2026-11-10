param(
 [Parameter(Mandatory)][string]$OutputDirectory,
 [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$frameworkRoot=Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET'
$compiler=@('Framework64','Framework') | ForEach-Object {Join-Path $frameworkRoot ($_+'\v4.0.30319\csc.exe')} | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
if(!$compiler){throw 'O compilador do .NET Framework do Windows não foi encontrado.'}
$work=Join-Path $root ('artifacts\latest-installer-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$attributes=Join-Path $work 'AssemblyInfo.cs'
@"
using System.Reflection;
[assembly:AssemblyTitle("Anime Upscale Studio Installer")]
[assembly:AssemblyCompany("ImCarL6")]
[assembly:AssemblyVersion("$Version.0")]
[assembly:AssemblyFileVersion("$Version.0")]
"@ | Set-Content -LiteralPath $attributes -Encoding utf8
$output=Join-Path $OutputDirectory 'AnimeUpscaleStudio-Instalar.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$output" "/win32icon:$(Join-Path $root 'src\Anime4KEncoder\painter.ico')" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Net.Http.dll /r:System.Web.Extensions.dll (Join-Path $root 'src\LatestInstaller\Program.cs') (Join-Path $root 'src\LatestInstaller\SelfTests.cs') $attributes
if($LASTEXITCODE){throw 'Falha ao compilar o instalador da versão mais recente.'}
$report=Join-Path $work 'self-test.txt'
$process=Start-Process -FilePath $output -ArgumentList @('--self-test',('"'+$report+'"')) -WindowStyle Hidden -PassThru
$process.WaitForExit()
if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $report) -or (Get-Content -LiteralPath $report -Raw) -notmatch 'LATEST_INSTALLER=PASS'){throw "Falha nos testes do instalador: $report"}
Write-Output "LATEST_INSTALLER=PASS: $output"
