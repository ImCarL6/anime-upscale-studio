param([Parameter(Mandatory)][string]$Package, [string]$Source = '', [string]$Output = '')
$ErrorActionPreference = 'Stop'
$Package = [IO.Path]::GetFullPath($Package)
Add-Type -AssemblyName PresentationFramework
Set-Location -LiteralPath $Package
[Environment]::CurrentDirectory = $Package
$env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $Package 'Anime4KEncoder.dll'))
$type = $assembly.GetType('Anime4KEncoder.MainWindow',$true)
$window = [Activator]::CreateInstance($type)
[Threading.SynchronizationContext]::SetSynchronizationContext([Windows.Threading.DispatcherSynchronizationContext]::new($window.Dispatcher))
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
if ($type.GetField('_root',$flags).GetValue($window) -ne $Package) { throw 'A cópia usou recursos de outra instalação.' }
$task = $type.GetMethod('CheckPortableEnvironmentAsync',$flags).Invoke($window,@())
while (!$task.IsCompleted) {
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $timer = [Windows.Threading.DispatcherTimer]::new()
    $timer.Interval = [TimeSpan]::FromMilliseconds(50)
    $timer.Add_Tick({$frame.Continue = $false})
    $timer.Start()
    [Windows.Threading.Dispatcher]::PushFrame($frame)
    $timer.Stop()
}
$task.GetAwaiter().GetResult()
if (!$type.GetField('_environmentReady',$flags).GetValue($window)) {throw 'Verificação inicial falhou.'}
$models = Get-ChildItem (Join-Path $Package '2x_AnimeJaNai_HD_V3_ModelsOnly') -File
if ($models.Count -ne 6 -or ($models | Where-Object Extension -ne '.onnx')) {throw 'Modelos/caches indevidos no pacote limpo.'}
if (!(Test-Path (Join-Path $Package 'coreclr.dll'))) {throw '.NET não incluído.'}
if (!(Test-Path (Join-Path $Package 'tools\videojanai\inference\nvinfer_builder_resource_sm120_11.dll'))) {throw 'SM120 ausente.'}
if ($Source -and $Output) {
    $preset = $type.GetField('_shaderPresets',$flags).GetValue($window)[0]
    $shader = $type.GetMethod('BuildCombinedShader',$flags).Invoke($window,@($preset))
    $arguments = $type.GetMethod('BuildFfmpegArguments',$flags).Invoke($window,@($Source,$Output,28,$shader))
    & (Join-Path $Package 'tools\ffmpeg\ffmpeg.exe') @arguments *> ($Output + '.log')
    if ($LASTEXITCODE) {throw "Anime4K falhou: $Output.log"}
    $media = & (Join-Path $Package 'tools\ffmpeg\ffprobe.exe') -v error -count_frames -select_streams v:0 -show_entries stream=codec_name,width,height,pix_fmt,nb_read_frames -of json $Output | ConvertFrom-Json
    $stream = $media.streams[0]
    if ($stream.codec_name -ne 'av1' -or $stream.width -ne 3840 -or $stream.height -ne 2160 -or $stream.nb_read_frames -ne 24) {throw 'Saída Anime4K inesperada.'}
    Write-Output 'ANIME4K_PORTABLE=PASS; AV1=3840x2160; FRAMES=24'
}
$window.Close()
Write-Output 'PORTABLE_STARTUP=PASS; ROOT=ISOLATED; PATH=WINDOWS_ONLY; MODELS=6; SM120=PRESENT'
