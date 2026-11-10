using Anime4KEncoder;

var temporary = Path.Combine(Path.GetTempPath(), "anime-studio-release-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var v1 = Path.Combine(temporary, "version1");
var v2 = Path.Combine(temporary, "version2");
var persistent = Path.Combine(temporary, "UserData");
foreach (var root in new[] { v1, v2 })
{
    Directory.CreateDirectory(Path.Combine(root, "2x_AnimeJaNai_HD_V3_ModelsOnly"));
    File.WriteAllText(Path.Combine(root, "runtime-manifest.json"), "same-runtime-v1");
    File.WriteAllText(Path.Combine(root, "2x_AnimeJaNai_HD_V3_ModelsOnly", "fixture.onnx"), "synthetic-model");
}
void Check(bool result, string message) { if (!result) throw new Exception(message); }
Check(StoragePaths.DataRoot(v1) == v1, "Portable storage changed.");
File.WriteAllText(Path.Combine(v1, "sq.version"), "installed-marker");
Check(!StoragePaths.DataRoot(v1).StartsWith(v1), "Installed data would be replaced by update.");
var models1 = StoragePaths.ModelRoot(v1, persistent);
var engine = Path.Combine(models1, "fixture.engine");
File.WriteAllText(engine, "compiled-on-this-gpu");
File.WriteAllText(Path.Combine(persistent, "history.json"), "user-history");
var models2 = StoragePaths.ModelRoot(v2, persistent);
Check(models1 == models2 && File.ReadAllText(engine) == "compiled-on-this-gpu", "App-only update lost engines.");
Check(File.ReadAllText(Path.Combine(models2, "fixture.onnx")) == "synthetic-model", "Model seed failed.");
File.WriteAllText(Path.Combine(v2, "runtime-manifest.json"), "changed-runtime-v2");
var models3 = StoragePaths.ModelRoot(v2, persistent);
Check(models3 != models1 && File.Exists(engine), "Runtime update reused engines or removed old cache.");
Check(!File.Exists(Path.Combine(models3, "fixture.engine")), "Incompatible engine was migrated.");
Check(File.ReadAllText(Path.Combine(persistent, "history.json")) == "user-history", "History was modified.");
File.Delete(Path.Combine(v2, "runtime-manifest.json"));
try { StoragePaths.ModelRoot(v2, persistent); throw new Exception("Missing manifest accepted."); }
catch (FileNotFoundException) { }
Console.WriteLine("RELEASE_STORAGE=PASS: portable, installed, app-only update, runtime invalidation, retained history, missing manifest.");
Console.WriteLine("Synthetic fixtures retained at: " + temporary);
var frames = ComparisonPlanner.Build(new[] { (100L, 120L), (1000L, 119L), (2000L, 1L) });
Check(frames[0].OriginalFrame == 160 && frames[0].PilotFrame == 60, "First sample alignment is wrong.");
Check(frames[1].OriginalFrame == 1059 && frames[1].PilotFrame == 179, "Odd-sized sample alignment is wrong.");
Check(frames[2].OriginalFrame == 2000 && frames[2].PilotFrame == 239, "Single-frame sample alignment is wrong.");
try { ComparisonPlanner.Build(new[] { (0L, 0L) }); throw new Exception("Empty sample accepted."); }
catch (ArgumentOutOfRangeException) { }
Console.WriteLine("COMPARISON_FRAME_ALIGNMENT=PASS: noncontiguous original offsets, concatenated pilot, odd sizes, short sample.");

var jobRoot = Path.Combine(temporary, ".anime-upscale-temp");
var successJob = Path.Combine(jobRoot, "resume-success");
var failedJob = Path.Combine(jobRoot, "resume-failed");
foreach (var job in new[] { successJob, failedJob })
{
    Directory.CreateDirectory(Path.Combine(job, "toolchain-fixture"));
    File.WriteAllText(Path.Combine(job, "toolchain-fixture", "segment.mkv"), "recoverable-segment");
}
Check(JobTemporaryFiles.Cleanup(failedJob, false) is null && Directory.Exists(failedJob), "Failed/cancelled work was removed.");
Check(JobTemporaryFiles.Cleanup(successJob, true) is null && !Directory.Exists(successJob), "Successful work was retained.");
Check(File.ReadAllText(Path.Combine(failedJob, "toolchain-fixture", "segment.mkv")) == "recoverable-segment", "Cleanup affected another job.");
Check(JobTemporaryFiles.Cleanup(successJob, true) is null, "Repeated cleanup failed.");
Check(JobTemporaryFiles.Cleanup(failedJob, true) is null && !Directory.Exists(jobRoot), "Empty temporary parent was retained.");
Check(JobTemporaryFiles.Cleanup(v1, true) is not null && Directory.Exists(v1), "Cleanup accepted a non-job directory.");
Console.WriteLine("JOB_TEMPORARY_CLEANUP=PASS: success, failure/cancellation, sibling isolation, repeated cleanup, empty parent, path guard.");

var timeline = new InputTimeline();
timeline.Add("codec_type=video|pts_time=9|duration_time=1");
timeline.Add("codec_type=audio|pts_time=9|duration_time=1");
timeline.Add("codec_type=subtitle|pts_time=11|duration_time=4");
timeline.Validate(15); // subtitles may legitimately extend beyond A/V
try { timeline.Validate(240); throw new Exception("Missing tail accepted."); } catch (InvalidOperationException) { }
try { InputTimeline.ValidateReader(0, "File ended prematurely"); throw new Exception("Successful exit hid truncation."); } catch (InvalidOperationException) { }
InputTimeline.ValidateReader(0, "");
const string equalDts = "[null @ 00000193f17300c0] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 32512 >= 32512";
InputTimeline.ValidateReader(0, equalDts + "\r\n" + equalDts, true);
foreach (var (code, diagnostic, allow) in new[] {
    (1, equalDts, true), (0, equalDts, false),
    (0, equalDts.Replace("32512 >= 32512", "32513 >= 32512"), true),
    (0, equalDts.Replace("[null @", "[matroska @"), true),
    (0, equalDts + "\nFile ended prematurely", true),
    (0, equalDts + "\nError while decoding stream #0:0: Invalid data", true) })
{
    try { InputTimeline.ValidateReader(code, diagnostic, allow); throw new Exception("Fatal diagnostic accepted: " + diagnostic); }
    catch (InvalidOperationException) { }
}
var visited = new List<int>();
var failedItems = new List<int>();
foreach (var index in new[] { 0, 1, 2 })
    await QueueExecution.RunItemAsync(() => {
        visited.Add(index);
        return index == 1 ? Task.FromException(new IOException("Broken input")) : Task.CompletedTask;
    }, _ => failedItems.Add(index), CancellationToken.None);
Check(visited.SequenceEqual(new[] { 0, 1, 2 }) && failedItems.SequenceEqual(new[] { 1 }), "File failure stopped queue or repeated item.");
foreach (var stop in new Exception[] { new OperationCanceledException(), new ResourcePauseException("Memory reserve exhausted") })
{
    try { await QueueExecution.RunItemAsync(() => Task.FromException(stop), _ => throw new Exception("Stop swallowed"), CancellationToken.None); throw new Exception("Queue stop ignored"); }
    catch (Exception ex) when (ReferenceEquals(ex, stop)) { }
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await QueueExecution.RunItemAsync(() => throw new Exception("Cancelled item started"), _ => { }, cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
}
Console.WriteLine("TIMESTAMP_AND_QUEUE_FAILURES=PASS: equal-DTS only, fatal diagnostics retained, next item continues, cancellation/resource pauses propagate.");

// Disco cheio pausa a fila inteira em vez de marcar cada episódio seguinte como "Falhou".
var diskFullFfmpeg = new InvalidOperationException("""
    [out#0/matroska @ 000001f152b1ccc0] Task finished with error code: -28 (No space left on device)
    Conversion failed!
    """);
Check(QueueExecution.IsDiskFull(diskFullFfmpeg), "FFmpeg disk-full output not recognized.");
Check(QueueExecution.IsDiskFull(new Exception("wrapper", new IOException("disk", unchecked((int)0x80070070)))), "ERROR_DISK_FULL HRESULT not recognized.");
Check(QueueExecution.IsDiskFull(new IOException("handle", unchecked((int)0x80070027))), "ERROR_HANDLE_DISK_FULL HRESULT not recognized.");
Check(QueueExecution.IsDiskFull("ExitCode=-28"), "FFmpeg exit code -28 not recognized.");
foreach (var unrelated in new[] { "A duração da saída difere da entrada: 00:20:03 vs. 00:23:47.", "Cannot allocate memory", "non monotonically increasing dts", "space 28" })
    Check(!QueueExecution.IsDiskFull(unrelated), "Unrelated failure classified as disk full: " + unrelated);
var diskPause = new DiskSpacePauseException(QueueExecution.DiskFullMessage);
var afterPause = new List<int>();
try
{
    foreach (var index in new[] { 0, 1, 2 })
        await QueueExecution.RunItemAsync(() => { afterPause.Add(index); return index == 0 ? Task.FromException(diskPause) : Task.CompletedTask; },
            _ => throw new Exception("Disk pause recorded as file failure"), CancellationToken.None);
    throw new Exception("Disk pause did not stop the queue");
}
catch (Exception ex) when (ReferenceEquals(ex, diskPause)) { }
Check(afterPause.SequenceEqual(new[] { 0 }), "Queue continued to later episodes after the disk filled.");
Console.WriteLine("DISK_FULL_PAUSE=PASS: FFmpeg -28 and Windows disk-full codes recognized, unrelated errors excluded, queue paused without failing later items.");
Console.WriteLine("INPUT_INTEGRITY=PASS: successful exit with truncation rejected, duration mismatch rejected, extended subtitles accepted.");
var sessionFile = Path.Combine(temporary, "session", "queue-session.json");
SessionStore.Save(sessionFile, new QueueSession { OutputDirectory = "output á", InputDirectory = "input", ElapsedSeconds = 123,
    Items = [new QueueEntry { InputPath = "épisode.mkv", Status = "Processando", Progress = 42,
        EstimatedOutputBytes = 4000000, EstimatedTotalSeconds = 1234, EstimateBasis = "3 execuções semelhantes" }] });
SessionStore.Save(sessionFile, new QueueSession { OutputDirectory = "updated", ElapsedSeconds = 150 });
Check(SessionStore.Load(sessionFile)!.ElapsedSeconds == 150, "Session latest state lost.");
File.WriteAllText(sessionFile, "broken-json");
var recovered = SessionStore.Load(sessionFile)!;
Check(recovered.ElapsedSeconds == 123 && recovered.Items.Single().InputPath == "épisode.mkv", "Session backup recovery failed.");
Check(recovered.Items[0].EstimatedOutputBytes == 4000000 && recovered.Items[0].EstimatedTotalSeconds == 1234 &&
    recovered.Items[0].EstimateBasis == "3 execuções semelhantes", "Original estimate lost across session recovery.");
Console.WriteLine("QUEUE_SESSION=PASS: atomic replacement, accumulated time, paths, queue and corrupted-primary recovery.");

var estimateNow = DateTimeOffset.UtcNow;
var estimateInput = new QueueEstimateInput(1000000000, 1200, 1920, 1080, 6666667, 2, false, "h264", "yuv420p");
var measurement = new HistoryRecord(estimateNow, "animejanai", "Balanced", 28, estimateInput.InputBytes,
    4000000000, 1200, 9600, InputBitRate: estimateInput.InputBitRate, AudioTracks: 2,
    InputVideoCodec: "h264", InputPixelFormat: "yuv420p", PipelineVersion: 1) { ResourcePercent = 80 };
AdaptiveEstimate Predict(IEnumerable<HistoryRecord> history, QueueEstimateInput? input = null) =>
    QueueEstimates.Predict(input ?? estimateInput, history.ToArray(), "animejanai", "Balanced", 28, 1,
        false, "", 1, 80, estimateNow);
var initialEstimate = Predict([]);
var previousEstimate = initialEstimate;
foreach (var sampleCount in new[] { 1, 2, 5, 20, 80 })
{
    var learned = Predict(Enumerable.Repeat(measurement, sampleCount));
    Check(Math.Abs(learned.TotalSeconds - measurement.ElapsedSeconds) < Math.Abs(previousEstimate.TotalSeconds - measurement.ElapsedSeconds),
        "Time estimate failed to converge after more completed episodes.");
    Check(Math.Abs(learned.OutputBytes - measurement.OutputBytes) < Math.Abs(previousEstimate.OutputBytes - measurement.OutputBytes),
        "Size estimate failed to converge after more completed episodes.");
    Check(learned.SampleCount == sampleCount && learned.Confidence > previousEstimate.Confidence,
        "Learning confidence failed to grow with comparable measurements.");
    previousEstimate = learned;
}
Check(Math.Abs(previousEstimate.TotalSeconds / measurement.ElapsedSeconds - 1) < 0.01,
    "Default guess retained too much influence after 80 completed episodes.");
var incompatible = new[] { measurement with { Engine = "anime4k" }, measurement with { ResourcePercent = 100 },
    measurement with { ResourcePercent = -1 }, measurement with { PipelineVersion = 0 },
    measurement with { IsBeta = true }, measurement with { BetaSignature = "other" },
    measurement with { DurationSeconds = double.NaN }, measurement with { ElapsedSeconds = double.PositiveInfinity } };
Check(Predict(incompatible) == initialEstimate, "Incompatible or invalid history affected estimates.");
var recent = measurement with { Timestamp = estimateNow.AddDays(-1), OutputBytes = 3000000000, ElapsedSeconds = 8000 };
var older = measurement with { Timestamp = estimateNow.AddDays(-2) };
Check(Predict(Enumerable.Repeat(older, 80).Append(recent)) == Predict(Enumerable.Repeat(older, 79).Append(recent)),
    "Old measurements crowded a newer comparable completion out of the history window.");
var doubled = Predict([measurement], estimateInput with { DurationSeconds = 2400, InputBytes = 2000000000 });
var single = Predict([measurement]);
Check(doubled.OutputBytes == single.OutputBytes * 2 && Math.Abs(doubled.TotalSeconds - single.TotalSeconds * 2) < 0.001,
    "Different episode durations were not normalized.");
Check(Predict([measurement with { Timestamp = estimateNow.AddDays(-90) }]).Confidence < single.Confidence,
    "Old history did not lose influence.");
Console.WriteLine("ADAPTIVE_QUEUE_ESTIMATES=PASS: sequential learning, convergence, compatibility, recent completions, duration scaling, age decay.");

Check(SegmentPlanning.Seconds(70) == 60 && SegmentPlanning.Seconds(80) == 180 && SegmentPlanning.Seconds(100) == 300, "Wrong live block durations.");
var layout = new List<SegmentCheckpoint>();
const double fractionalFps = 24000d / 1001;
const long totalFrames = 20003;
var expectedStart = 0L;
var modes = new[] { 70, 100, 80, 70, 100, 80 };
for (var i = 0; expectedStart < totalFrames; i++)
{
    var mode = modes[i % modes.Length];
    var before = layout.Select(c => (c.UsefulStartFrame, c.UsefulEndFrame)).ToArray();
    var block = SegmentPlanning.SelectNext(layout, i, totalFrames, fractionalFps, mode, 2, i - 1);
    Check(block.UsefulStartFrame == expectedStart, "Live replan created a gap/duplicate.");
    var expectedLength = Math.Min(totalFrames - expectedStart, (long)Math.Round(fractionalFps * SegmentPlanning.Seconds(mode), MidpointRounding.AwayFromZero));
    Check(block.UsefulFrameCount == expectedLength, "New profile was not applied at the next boundary.");
    Check(before.SequenceEqual(layout.Take(i).Select(c => (c.UsefulStartFrame, c.UsefulEndFrame))), "Live replan changed completed boundaries.");
    Check(block.PrefixOverlapFrames == (i == 0 ? 0 : 2), "Overlap was lost at a dynamic boundary.");
    expectedStart = block.UsefulEndFrame;
}
Check(layout.Sum(c => c.UsefulEndFrame - c.UsefulStartFrame) == totalFrames && SegmentPlanning.ValidLayout(layout, totalFrames, 2), "Mixed layout does not cover exactly the input.");
var serialized = System.Text.Json.JsonSerializer.Serialize(new SegmentManifest { LayoutPolicyVersion = 1, Segments = layout });
var restoredLayout = System.Text.Json.JsonSerializer.Deserialize<SegmentManifest>(serialized)!;
Check(SegmentPlanning.ValidLayout(restoredLayout.Segments, totalFrames, 2), "Mixed layout was lost on restart.");
// A fixed 60-second legacy plan can retain cached blocks, then grow its remaining blocks.
var legacy = new List<SegmentCheckpoint>();
for (var i = 0; i < 10; i++)
{
    var b = SegmentPlanning.Create(i, i * 1440, 14400, 1440, 2);
    legacy.Add(new() { Index = i, UsefulStartFrame = b.UsefulStartFrame, UsefulEndFrame = b.UsefulEndFrame, SourceStartFrame = b.SourceStartFrame, SourceEndFrame = b.SourceEndFrame });
}
var cached = SegmentPlanning.SelectNext(legacy, 0, 14400, 24, 100, 2, 2);
Check(cached.UsefulEndFrame == 1440 && legacy.Count == 10, "Cached legacy block was expanded.");
var repair = SegmentPlanning.SelectNext(legacy, 1, 14400, 24, 100, 2, 2);
Check(repair.UsefulStartFrame == 1440 && repair.UsefulEndFrame == 2880, "Repair invalidated later cached boundaries.");
var grown = SegmentPlanning.SelectNext(legacy, 3, 14400, 24, 100, 2, 2);
Check(grown.UsefulStartFrame == 4320 && grown.UsefulEndFrame == 11520 && legacy.Count == 4, "Unprocessed legacy tail was not replanned to five minutes.");
var retried = SegmentPlanning.SelectNext(legacy, 3, 14400, 24, 70, 2, 2);
Check(retried.UsefulStartFrame == 4320 && retried.UsefulEndFrame == 5760, "Unfinished block did not adapt safely after restart.");
var broken = legacy.Select(c => new SegmentCheckpoint { Index = c.Index, UsefulStartFrame = c.UsefulStartFrame, UsefulEndFrame = c.UsefulEndFrame, SourceStartFrame = c.SourceStartFrame, SourceEndFrame = c.SourceEndFrame }).ToList();
broken[1].UsefulStartFrame++;
Check(!SegmentPlanning.ValidLayout(broken, 14400, 2), "A missing frame was accepted.");
try { SegmentPlanning.SelectNext(broken, 1, 14400, 24, 80, 2, 2); throw new Exception("Broken recovery was accepted."); }
catch (InvalidOperationException) { }
Check(SegmentPlanning.ValidLayout([], 1, 2), "Fresh plan was rejected.");
var oneFrame = SegmentPlanning.SelectNext([], 0, 1, fractionalFps, 100, 2, -1);
Check(oneFrame.UsefulFrameCount == 1 && oneFrame.SourceFrameCount == 1, "Short final segment is invalid.");
Console.WriteLine("DYNAMIC_SEGMENTS=PASS: 1/3/5 minutes, fractional CFR, both mode directions, exact coverage and overlaps, immutable prefix, persisted mixed layout, legacy cache/repair, pending retry and invalid-layout rejection.");

var speculative = SegmentPlanning.Create(1, 7200, 20000, 7200, 2);
var preparedSource = new PreparedSource("synthetic-source", 1234, 2.5);
await using (var pending = new SourcePrefetch(speculative, CancellationToken.None, _ => Task.FromResult(preparedSource)))
    Check(await pending.TakeAsync(speculative, CancellationToken.None) == preparedSource, "Matching speculation was not reused.");
var stopped = false;
await using (var pending = new SourcePrefetch(speculative, CancellationToken.None, async token =>
{
    try { await Task.Delay(Timeout.Infinite, token); return preparedSource; }
    finally { stopped = true; }
}))
{
    var shorter = SegmentPlanning.Create(1, 7200, 20000, 1440, 2);
    Check(await pending.TakeAsync(shorter, CancellationToken.None) is null, "Mode change reused wrong frames.");
}
Check(stopped, "Abandoned preparation was not cancelled and drained.");
using (var parent = new CancellationTokenSource())
{
    await using var pending = new SourcePrefetch(speculative, parent.Token, async token =>
    { await Task.Delay(Timeout.Infinite, token); return preparedSource; });
    parent.Cancel();
    try { await pending.TakeAsync(speculative, parent.Token); throw new Exception("Parent cancellation ignored."); }
    catch (OperationCanceledException) { }
}
await using (var pending = new SourcePrefetch(speculative, CancellationToken.None, _ => Task.FromResult(preparedSource)))
{
    pending.Cancel();
    Check(await pending.TakeAsync(speculative, CancellationToken.None) is null, "Reduced mode adopted cancelled speculation.");
}
await using (var pending = new SourcePrefetch(speculative, CancellationToken.None, _ => Task.FromException<PreparedSource>(diskFullFfmpeg)))
{
    try { await pending.TakeAsync(speculative, CancellationToken.None); throw new Exception("Disk-full swallowed."); }
    catch (Exception ex) when (ReferenceEquals(ex, diskFullFfmpeg)) { }
}
await using (var pending = new SourcePrefetch(speculative, CancellationToken.None, _ => Task.FromException<PreparedSource>(new IOException("abandoned"))))
    Check(await pending.TakeAsync(speculative with { SourceEndFrame = speculative.SourceEndFrame - 1 }, CancellationToken.None) is null,
        "Invalid speculation was adopted.");
Console.WriteLine("SOURCE_PREFETCH=PASS: exact plan reuse, mode change, cancellation/drain, reduced mode, disk-full propagation, abandoned failure.");
