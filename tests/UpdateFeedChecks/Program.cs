using System.Text.Json.Nodes;
using Velopack;
using Velopack.Locators;

if (args.Length != 2) throw new ArgumentException("Usage: UpdateFeedChecks <local-release-feed> <test-output-directory>");
var feed = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
var locator = new TestVelopackLocator("ImCarL6.AnimeUpscaleStudio", "0.0.0", Path.Combine(output, "packages"));
var manager = new UpdateManager(feed, new UpdateOptions { ExplicitChannel = "win" }, locator);
var update = await manager.CheckForUpdatesAsync() ?? throw new Exception("Valid feed did not offer an update.");
if (string.IsNullOrWhiteSpace(update.TargetFullRelease.NotesMarkdown)) throw new Exception("Release notes missing.");
await manager.DownloadUpdatesAsync(update);
Console.WriteLine("UPDATE_FEED_DOWNLOAD=PASS: " + update.TargetFullRelease.Version);
var current = new UpdateManager(feed, new UpdateOptions { ExplicitChannel = "win" },
    new TestVelopackLocator("ImCarL6.AnimeUpscaleStudio", update.TargetFullRelease.Version.ToString(), Path.Combine(output, "current")));
if (await current.CheckForUpdatesAsync() is not null) throw new Exception("Current version offered itself again.");
Console.WriteLine("UPDATE_CURRENT_VERSION=PASS");

var corruptFeed = Path.Combine(output, "corrupt-feed");
Directory.CreateDirectory(corruptFeed);
var document = JsonNode.Parse(File.ReadAllText(Path.Combine(feed, "releases.win.json")))!;
var asset = document["Assets"]!.AsArray().First(x => x!["Type"]!.GetValue<string>() == "Full")!;
document["Assets"] = new JsonArray(asset.DeepClone());
File.WriteAllText(Path.Combine(corruptFeed, "releases.win.json"), document.ToJsonString());
File.WriteAllText(Path.Combine(corruptFeed, asset["FileName"]!.GetValue<string>()), "corrupt-package");
var broken = new UpdateManager(corruptFeed, new UpdateOptions { ExplicitChannel = "win" },
    new TestVelopackLocator("ImCarL6.AnimeUpscaleStudio", "0.0.0", Path.Combine(output, "bad-packages")));
var brokenUpdate = await broken.CheckForUpdatesAsync() ?? throw new Exception("Test feed invalid.");
bool rejected = false;
try { await broken.DownloadUpdatesAsync(brokenUpdate); }
catch (Exception ex) when (ex.GetType().Name.Contains("Checksum") || ex.Message.Contains("size", StringComparison.OrdinalIgnoreCase)) { rejected = true; }
if (!rejected) throw new Exception("Corrupt package was not rejected.");
Console.WriteLine("UPDATE_CORRUPT_PACKAGE=REJECTED; NO_APPLY_PERFORMED");
