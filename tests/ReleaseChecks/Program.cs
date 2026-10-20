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
Console.WriteLine("INPUT_INTEGRITY=PASS: successful exit with truncation rejected, duration mismatch rejected, extended subtitles accepted.");
var sessionFile = Path.Combine(temporary, "session", "queue-session.json");
SessionStore.Save(sessionFile, new QueueSession { OutputDirectory = "output á", InputDirectory = "input", ElapsedSeconds = 123,
    Items = [new QueueEntry { InputPath = "épisode.mkv", Status = "Processando", Progress = 42 }] });
SessionStore.Save(sessionFile, new QueueSession { OutputDirectory = "updated", ElapsedSeconds = 150 });
Check(SessionStore.Load(sessionFile)!.ElapsedSeconds == 150, "Session latest state lost.");
File.WriteAllText(sessionFile, "broken-json");
var recovered = SessionStore.Load(sessionFile)!;
Check(recovered.ElapsedSeconds == 123 && recovered.Items.Single().InputPath == "épisode.mkv", "Session backup recovery failed.");
Console.WriteLine("QUEUE_SESSION=PASS: atomic replacement, accumulated time, paths, queue and corrupted-primary recovery.");

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
