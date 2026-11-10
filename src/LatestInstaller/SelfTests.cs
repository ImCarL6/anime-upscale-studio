using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeUpscaleStudioInstaller
{
    internal static class SelfTests
    {
        private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        private static string Fixture(string version, string hash)
        {
            return "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"" + version + "\",\"assets\":[{\"name\":\"" + Release.SetupName + "\",\"state\":\"uploaded\",\"size\":7,\"digest\":\"sha256:" + hash + "\",\"browser_download_url\":\"https://github.com/" + Release.Repository + "/releases/download/" + version + "/" + Release.SetupName + "\"}]}";
        }
        private static void Reject(string json)
        {
            try { Release.Parse(json); }
            catch { return; }
            throw new Exception("Unsafe release accepted.");
        }
        internal static void Run()
        {
            var hash = new string('a', 64);
            var json = Fixture("v99.1.0", hash);
            Check(Release.Parse(json).Version == "v99.1.0", "A future release was not selected from metadata.");
            Check(Release.Parse(Fixture("v101.2.3", hash)).Version == "v101.2.3", "Installer kept an embedded version.");
            Reject(json.Replace("\"draft\":false", "\"draft\":true"));
            Reject(json.Replace("\"prerelease\":false", "\"prerelease\":true"));
            Reject(json.Replace("https://github.com/", "http://github.com/"));
            Reject(json.Replace("https://github.com/", "https://evil.example/"));
            Reject(json.Replace("/releases/download/v99.1.0/", "/releases/download/v0.1.0/"));
            Reject(json.Replace("sha256:" + hash, ""));
            Reject(json.Replace("\"size\":7", "\"size\":0"));
            Reject(json.Replace("\"uploaded\"", "\"new\""));
            Reject(json.Replace(Release.SetupName, "another.exe"));
            var root = Path.Combine(Path.GetTempPath(), "AnimeUpscaleStudioInstaller-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var bytes = Encoding.UTF8.GetBytes("fixture");
                string digest;
                using (var sha = SHA256.Create()) digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                var release = Release.Parse(Fixture("v99.1.0", digest));
                var output = Path.Combine(root, "setup.exe");
                Download.SaveVerified(new MemoryStream(bytes), output, release, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(File.Exists(output) && !File.Exists(output + ".partial"), "Verified download was not promoted.");
                File.Delete(output);
                foreach (var bad in new[] { Encoding.UTF8.GetBytes("tamper!"), Encoding.UTF8.GetBytes("short"), Encoding.UTF8.GetBytes("too long!") })
                {
                    var rejected = false;
                    try { Download.SaveVerified(new MemoryStream(bad), output, release, null, CancellationToken.None).GetAwaiter().GetResult(); }
                    catch (InvalidDataException) { rejected = true; }
                    Check(rejected && !File.Exists(output) && !File.Exists(output + ".partial"), "Corrupt/truncated download was promoted or left behind.");
                }
                using (var cts = new CancellationTokenSource())
                {
                    cts.Cancel();
                    var cancelled = false;
                    try { Download.SaveVerified(new MemoryStream(bytes), output, release, null, cts.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Check(cancelled && !File.Exists(output) && !File.Exists(output + ".partial"), "Cancelled download was promoted or left behind.");
                }
                File.WriteAllText(output + ".partial", "unrelated existing file");
                try { Download.SaveVerified(new MemoryStream(bytes), output, release, null, CancellationToken.None).GetAwaiter().GetResult(); }
                catch (IOException) { }
                Check(File.ReadAllText(output + ".partial") == "unrelated existing file", "An unrelated partial was overwritten or removed.");
                File.Delete(output + ".partial");
                var calls = 0;
                Download.Fetch(release, output, null, CancellationToken.None, delegate(string partial, CancellationToken token)
                {
                    calls++;
                    if (calls == 1) { File.WriteAllText(partial, "fix"); throw new IOException("simulated interruption"); }
                    Check(File.ReadAllText(partial) == "fix", "Existing bytes were discarded during retry.");
                    File.AppendAllText(partial, "ture");
                    return Task.FromResult(0);
                }).GetAwaiter().GetResult();
                Check(calls == 2 && File.ReadAllText(output) == "fixture", "Interrupted download was not resumed cleanly.");
                File.Delete(output);
                var corruptRejected = false;
                try
                {
                    Download.Fetch(release, output, null, CancellationToken.None, delegate(string partial, CancellationToken token)
                    { File.WriteAllText(partial, "tamper!"); return Task.FromResult(0); }).GetAwaiter().GetResult();
                }
                catch (InvalidDataException) { corruptRejected = true; }
                Check(corruptRejected && !File.Exists(output) && !File.Exists(output + ".partial"), "Corrupt resumed download was promoted.");
                calls = 0;
                try
                {
                    Download.Fetch(release, output, null, CancellationToken.None, delegate(string partial, CancellationToken token)
                    { calls++; throw new IOException("permanent outage"); }).GetAwaiter().GetResult();
                }
                catch (IOException) { }
                Check(calls == 3 && !File.Exists(output) && !File.Exists(output + ".partial"), "Retry budget or failed-download cleanup is wrong.");
            }
            finally { Directory.Delete(root, true); }
        }

    }
}
