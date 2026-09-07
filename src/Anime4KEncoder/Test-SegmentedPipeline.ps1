param(
    [Parameter(Mandatory)][string]$InputPath,
    [int]$TestClipSeconds = 20,
    [int]$TestSegmentSeconds = 10,
    [string]$ModelName = '2x_AnimeJaNai_HD_V3.1Sharp1_Performance_SPANF3_b5f48_unshuffle_fp16',
    [string]$WorkDirectory = '',
    [int]$StopAfterSegments = 0
)

$ErrorActionPreference = 'Stop'
$studioRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$ffmpeg = Join-Path $studioRoot 'tools\ffmpeg\ffmpeg.exe'
$ffprobe = Join-Path $studioRoot 'tools\ffmpeg\ffprobe.exe'
$inference = Join-Path $studioRoot 'tools\videojanai\inference'
$aji = Join-Path $inference 'aji_encode.exe'
$trtexec = Join-Path $inference 'trtexec.exe'
$models = Join-Path $studioRoot '2x_AnimeJaNai_HD_V3_ModelsOnly'
$work = if ([string]::IsNullOrWhiteSpace($WorkDirectory)) {
    Join-Path $studioRoot ('validation\segmented-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
} else {
    [IO.Path]::GetFullPath($WorkDirectory)
}
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Invoke-Process([string]$Executable, [string[]]$Arguments, [string]$WorkingDirectory) {
    $info = [Diagnostics.ProcessStartInfo]::new($Executable)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.WorkingDirectory = $WorkingDirectory
    for ($argumentIndex = 0; $argumentIndex -lt $Arguments.Count; $argumentIndex++) {
        $argument = $Arguments[$argumentIndex]
        if ($null -eq $argument) { throw "Argumento nulo na posição $argumentIndex ao executar $Executable" }
        [void]$info.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $outText = $stdout.GetAwaiter().GetResult()
    $errText = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Processo falhou ($($process.ExitCode)): $errText" }
    [pscustomobject]@{ StdOut = $outText; StdErr = $errText; ExitCode = $process.ExitCode }
}

function Get-Structure([string]$Path) {
    $result = Invoke-Process $ffprobe @(
        '-v','error','-count_packets','-show_entries',
        'format=duration:stream=codec_type,codec_name,width,height,pix_fmt,avg_frame_rate,r_frame_rate,nb_read_packets:stream_tags=language,title,filename,mimetype:stream_disposition:chapter=id',
        '-of','json',$Path
    ) (Split-Path $ffprobe)
    $json = $result.StdOut | ConvertFrom-Json
    $video = $json.streams | Where-Object codec_type -eq 'video' | Select-Object -First 1
    $rateParts = $video.avg_frame_rate -split '/'
    $rate = [double]$rateParts[0] / [double]$rateParts[1]
    [pscustomobject]@{
        Duration = [double]$json.format.duration
        Frames = [long]$video.nb_read_packets
        FrameRate = $rate
        Codec = $video.codec_name
        PixelFormat = $video.pix_fmt
        Width = [int]$video.width
        Height = [int]$video.height
        Audio = @($json.streams | Where-Object codec_type -eq 'audio').Count
        Subtitle = @($json.streams | Where-Object codec_type -eq 'subtitle').Count
        Attachment = @($json.streams | Where-Object codec_type -eq 'attachment').Count
        Chapters = @($json.chapters).Count
        StreamIdentities = @($json.streams | ForEach-Object { Get-StreamIdentity $_ })
    }
}

function Get-StreamIdentity($Stream) {
    $tags = $Stream.tags
    $activeDispositions = @($Stream.disposition.PSObject.Properties |
        Where-Object { [int]$_.Value -ne 0 } |
        Sort-Object Name |
        ForEach-Object Name)
    [pscustomobject]@{
        Type = [string]$Stream.codec_type
        Language = if ($null -ne $tags) { [string]$tags.language } else { '' }
        Title = if ($null -ne $tags) { [string]$tags.title } else { '' }
        FileName = if ($null -ne $tags) { [string]$tags.filename } else { '' }
        MimeType = if ($null -ne $tags) { [string]$tags.mimetype } else { '' }
        Disposition = if ($activeDispositions.Count -eq 0) { '0' } else { $activeDispositions -join '+' }
    }
}

function Add-StreamPreservationArguments(
    [Collections.Generic.List[string]]$Arguments,
    $Stream,
    [string]$Specifier
) {
    $Arguments.Add("-disposition:$Specifier")
    $Arguments.Add([string]$Stream.Disposition)
    $Arguments.Add("-metadata:s:$Specifier")
    $Arguments.Add('language=' + [string]$Stream.Language)
    $Arguments.Add("-metadata:s:$Specifier")
    $Arguments.Add('title=' + [string]$Stream.Title)
    if ($Stream.Type -eq 'attachment') {
        $Arguments.Add("-metadata:s:$Specifier")
        $Arguments.Add('filename=' + [string]$Stream.FileName)
        $Arguments.Add("-metadata:s:$Specifier")
        $Arguments.Add('mimetype=' + [string]$Stream.MimeType)
    }
}

function Invoke-AjiPipe([string]$Source, [string]$Destination, [string]$Config, [long]$Prefix, [long]$UsefulFrames, [int]$Cq) {
    $pipeName = 'animejanai-smoke-' + [Guid]::NewGuid().ToString('N') + '.mkv'
    $pipePath = '\\.\pipe\' + $pipeName
    $server = [IO.Pipes.NamedPipeServerStream]::new(
        $pipeName,
        [IO.Pipes.PipeDirection]::In,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous,
        1048576,
        1048576)
    try {
        $ajiArgs = @(
            '--input',$Source,'--output',$pipePath,'--conf',$Config,'--slot','1',
            '--model-dir',$models,'--trtexec',$trtexec,'--backend','tensorrt',
            '--vcodec','hevc_nvenc','--vquality','-preset p7 -tune lossless -rc constqp -qp 0',
            '--pix-fmt','yuv420p10','--no-audio','--no-subs','--no-chapters','--overwrite','--progress','line')
        $endFrame = $Prefix + $UsefulFrames
        $ffArgs = @(
            '-hide_banner','-nostdin','-y','-fflags','+genpts','-i','pipe:0','-map','0:v:0',
            '-vf',"trim=start_frame=$Prefix`:end_frame=$endFrame,setpts=PTS-STARTPTS",'-an','-sn','-dn',
            '-c:v:0','av1_nvenc','-preset','p7','-tune','uhq','-rc','vbr','-b:v','0','-cq',"$Cq",
            '-multipass','fullres','-rc-lookahead','32','-lookahead_level','3','-spatial-aq','1',
            '-temporal-aq','1','-aq-strength','8','-pix_fmt','p010le','-highbitdepth','1',
            '-fps_mode','passthrough','-avoid_negative_ts','make_zero','-max_interleave_delta','0',
            '-max_muxing_queue_size','4096','-progress','pipe:1','-nostats',$Destination)

        $ajiInfo = [Diagnostics.ProcessStartInfo]::new($aji)
        $ajiInfo.UseShellExecute = $false
        $ajiInfo.CreateNoWindow = $true
        $ajiInfo.RedirectStandardOutput = $true
        $ajiInfo.RedirectStandardError = $true
        $ajiInfo.WorkingDirectory = $inference
        foreach ($argument in $ajiArgs) { [void]$ajiInfo.ArgumentList.Add($argument) }

        $ffInfo = [Diagnostics.ProcessStartInfo]::new($ffmpeg)
        $ffInfo.UseShellExecute = $false
        $ffInfo.CreateNoWindow = $true
        $ffInfo.RedirectStandardInput = $true
        $ffInfo.RedirectStandardOutput = $true
        $ffInfo.RedirectStandardError = $true
        $ffInfo.WorkingDirectory = Split-Path $ffmpeg
        foreach ($argument in $ffArgs) { [void]$ffInfo.ArgumentList.Add($argument) }

        $ajiProcess = [Diagnostics.Process]::new()
        $ajiProcess.StartInfo = $ajiInfo
        $ffProcess = [Diagnostics.Process]::new()
        $ffProcess.StartInfo = $ffInfo
        $connection = $server.WaitForConnectionAsync()
        [void]$ffProcess.Start()
        [void]$ajiProcess.Start()
        $ajiOut = $ajiProcess.StandardOutput.ReadToEndAsync()
        $ajiErr = $ajiProcess.StandardError.ReadToEndAsync()
        $ffOut = $ffProcess.StandardOutput.ReadToEndAsync()
        $ffErr = $ffProcess.StandardError.ReadToEndAsync()
        [void]$connection.GetAwaiter().GetResult()
        [void]$server.CopyToAsync($ffProcess.StandardInput.BaseStream, 1048576).GetAwaiter().GetResult()
        $ffProcess.StandardInput.Close()
        $ajiProcess.WaitForExit()
        $ffProcess.WaitForExit()
        $ajiOutText = $ajiOut.GetAwaiter().GetResult()
        $ajiErrText = $ajiErr.GetAwaiter().GetResult()
        $ffOutText = $ffOut.GetAwaiter().GetResult()
        $ffErrText = $ffErr.GetAwaiter().GetResult()
        [IO.File]::AppendAllText((Join-Path $work 'smoke.log'), "`nAJI:`n$ajiOutText`n$ajiErrText`nFFmpeg:`n$ffOutText`n$ffErrText")
        if ($ajiProcess.ExitCode -ne 0) { throw "AnimeJaNai falhou: $ajiErrText" }
        if ($ffProcess.ExitCode -ne 0) { throw "FFmpeg do bloco falhou: $ffErrText" }
    }
    finally {
        $server.Dispose()
    }
}

if (-not (Test-Path -LiteralPath $InputPath)) { throw "Entrada não encontrada: $InputPath" }
$testSource = Join-Path $work 'test-source.mkv'
if (-not (Test-Path -LiteralPath $testSource)) {
    $inputInfo = Get-Structure $InputPath
    $clipArguments = [Collections.Generic.List[string]]@(
        '-hide_banner','-nostdin','-y','-i',$InputPath,'-t',"$TestClipSeconds",
        '-map','0:v:0','-map','0:a?','-map','0:s?','-map','0:t?',
        '-map_metadata','0','-map_chapters','0','-c','copy')
    for ($subtitle = 0; $subtitle -lt $inputInfo.Subtitle; $subtitle++) {
        $clipArguments.Add("-disposition:s:$subtitle")
        $clipArguments.Add('0')
    }
    $clipArguments.Add($testSource)
    Invoke-Process $ffmpeg $clipArguments.ToArray() (Split-Path $ffmpeg) | Out-Null
}

$sourceInfo = Get-Structure $testSource
$segmentFrames = [long][Math]::Round($sourceInfo.FrameRate * $TestSegmentSeconds, [MidpointRounding]::AwayFromZero)
$segmentCount = [int][Math]::Ceiling($sourceInfo.Frames / [double]$segmentFrames)
$config = Join-Path $work ($ModelName + '.conf')
@"
[global]
config_version=2
logging=yes
backend=TensorRT

[slot_1]
profile_name=encode
chain_1_min_resolution=0x0
chain_1_max_resolution=0x0
chain_1_min_fps=0
chain_1_max_fps=0
chain_1_model_1_resize_height_before_upscale=0
chain_1_model_1_resize_factor_before_upscale=0
chain_1_model_1_name=$ModelName
chain_1_rife=no
chain_1_rife_model=
chain_1_rife_factor_numerator=2
chain_1_rife_factor_denominator=1
chain_1_rife_scene_detect_threshold=0.2
chain_1_rife_ensemble=no
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM

$concatLines = [Collections.Generic.List[string]]::new()
$concatLines.Add('ffconcat version 1.0')
$completedThisRun = 0
$recoveredSegments = 0
for ($index = 0; $index -lt $segmentCount; $index++) {
    $usefulStart = [long]$index * $segmentFrames
    $usefulEnd = [Math]::Min($sourceInfo.Frames, $usefulStart + $segmentFrames)
    $sourceStart = [Math]::Max(0, $usefulStart - 2)
    $sourceEnd = [Math]::Min($sourceInfo.Frames, $usefulEnd + 2)
    $prefix = $usefulStart - $sourceStart
    $sourceCount = $sourceEnd - $sourceStart
    $usefulCount = $usefulEnd - $usefulStart
    $sourceStartSeconds = $sourceStart / $sourceInfo.FrameRate
    $sourceSegment = Join-Path $work ('source-{0:0000}.mkv' -f ($index + 1))
    $outputSegment = Join-Path $work ('segment-{0:0000}.mkv' -f ($index + 1))
    if (Test-Path -LiteralPath $outputSegment) {
        $existing = Get-Structure $outputSegment
        if ($existing.Frames -eq $usefulCount -and $existing.Codec -eq 'av1' -and $existing.Width -eq 3840 -and $existing.Height -eq 2160) {
            $concatLines.Add("file 'segment-{0:0000}.mkv'" -f ($index + 1))
            $recoveredSegments++
            continue
        }
        Remove-Item -LiteralPath $outputSegment -Force
    }
    Invoke-Process $ffmpeg @(
        '-hide_banner','-nostdin','-y','-ss',($sourceStartSeconds.ToString('0.#########',[Globalization.CultureInfo]::InvariantCulture)),
        '-i',$testSource,'-map','0:v:0','-frames:v',"$sourceCount",'-an','-sn','-dn','-map_metadata','-1','-map_chapters','-1','-vf','setpts=PTS-STARTPTS',
        '-c:v','hevc_nvenc','-preset','p7','-tune','lossless','-rc','constqp','-qp','0','-pix_fmt','yuv420p',
        '-fps_mode','passthrough','-avoid_negative_ts','make_zero',$sourceSegment) (Split-Path $ffmpeg) | Out-Null
    $prepared = Get-Structure $sourceSegment
    if ($prepared.Frames -ne $sourceCount) { throw "Trecho fonte $($index + 1): $($prepared.Frames) frames; esperado $sourceCount" }
    Invoke-AjiPipe $sourceSegment $outputSegment $config $prefix $usefulCount 18
    $encoded = Get-Structure $outputSegment
    if ($encoded.Frames -ne $usefulCount -or $encoded.Codec -ne 'av1' -or $encoded.Width -ne 3840 -or $encoded.Height -ne 2160) {
        throw "Bloco AV1 $($index + 1) inválido: $($encoded | ConvertTo-Json -Compress)"
    }
    $concatLines.Add("file 'segment-{0:0000}.mkv'" -f ($index + 1))
    Remove-Item -LiteralPath $sourceSegment -Force
    $completedThisRun++
    if ($StopAfterSegments -gt 0 -and $completedThisRun -ge $StopAfterSegments) {
        [pscustomobject]@{
            WorkDirectory = $work
            Status = 'Interrompido propositalmente após bloco validado'
            CompletedThisRun = $completedThisRun
            RecoveredSegments = $recoveredSegments
            TotalSegments = $segmentCount
        } | ConvertTo-Json
        return
    }
}

$concat = Join-Path $work 'segments.ffconcat'
$concatLines | Set-Content -LiteralPath $concat -Encoding utf8NoBOM
$final = Join-Path $work 'segmented-final.mkv'
$finalArguments = [Collections.Generic.List[string]]@(
    '-hide_banner','-nostdin','-y','-f','concat','-safe','0','-i',$concat,'-i',$testSource,
    '-map','0:v:0','-map','1:a?','-map','1:s?','-map','1:t?','-map_metadata','1','-map_chapters','1','-c','copy',
    '-c:a','aac','-q:a','2','-ar:a','48000','-af:a','aresample=async=1:first_pts=0',
    '-avoid_negative_ts','make_zero','-max_interleave_delta','0','-max_muxing_queue_size','4096')
$sourceStreams = @($sourceInfo.StreamIdentities)
Add-StreamPreservationArguments $finalArguments ($sourceStreams | Where-Object Type -eq 'video' | Select-Object -First 1) 'v:0'
foreach ($typeInfo in @(
    [pscustomobject]@{ Type = 'audio'; Specifier = 'a' },
    [pscustomobject]@{ Type = 'subtitle'; Specifier = 's' },
    [pscustomobject]@{ Type = 'attachment'; Specifier = 't' }
)) {
    $streamIndex = 0
    foreach ($stream in @($sourceStreams | Where-Object Type -eq $typeInfo.Type)) {
        Add-StreamPreservationArguments $finalArguments $stream "$($typeInfo.Specifier):$streamIndex"
        $streamIndex++
    }
}
$finalArguments.Add($final)
Invoke-Process $ffmpeg $finalArguments.ToArray() (Split-Path $ffmpeg) | Out-Null

$finalInfo = Get-Structure $final
if ($finalInfo.Frames -ne $sourceInfo.Frames) { throw "Saída final: $($finalInfo.Frames) frames; entrada: $($sourceInfo.Frames)" }
if ($finalInfo.Audio -ne $sourceInfo.Audio -or $finalInfo.Subtitle -ne $sourceInfo.Subtitle -or
    $finalInfo.Attachment -ne $sourceInfo.Attachment -or $finalInfo.Chapters -ne $sourceInfo.Chapters) {
    throw "Inventário final diferente da entrada. Entrada=$($sourceInfo | ConvertTo-Json -Compress) Saída=$($finalInfo | ConvertTo-Json -Compress)"
}
$sourceIdentityJson = @($sourceInfo.StreamIdentities) | ConvertTo-Json -Compress -Depth 5
$finalIdentityJson = @($finalInfo.StreamIdentities) | ConvertTo-Json -Compress -Depth 5
if ($finalIdentityJson -cne $sourceIdentityJson) {
    throw "Metadados/disposições diferentes. Entrada=$sourceIdentityJson Saída=$finalIdentityJson"
}
for ($audio = 0; $audio -lt $finalInfo.Audio; $audio++) {
    Invoke-Process $ffmpeg @('-hide_banner','-nostdin','-v','error','-i',$final,'-map',"0:a:$audio",'-f','null','NUL') (Split-Path $ffmpeg) | Out-Null
}

[pscustomobject]@{
    WorkDirectory = $work
    InputFrames = $sourceInfo.Frames
    OutputFrames = $finalInfo.Frames
    FrameRate = $sourceInfo.FrameRate
    Segments = $segmentCount
    CompletedThisRun = $completedThisRun
    RecoveredSegments = $recoveredSegments
    AudioStreams = $finalInfo.Audio
    SubtitleStreams = $finalInfo.Subtitle
    AttachmentStreams = $finalInfo.Attachment
    Chapters = $finalInfo.Chapters
    FinalPath = $final
} | ConvertTo-Json
