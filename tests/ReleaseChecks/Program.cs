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
