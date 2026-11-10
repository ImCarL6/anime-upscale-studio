using System;
using System.IO;
using System.Net;
using System.Net.Http;
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
                var transport = new RangeTransport(bytes, true, false);
                Download.Fetch(release, output, null, CancellationToken.None, new HttpClient(transport)).GetAwaiter().GetResult();
                Check(transport.Calls == 2 && File.ReadAllText(output) == "fixture", "Interrupted range was not retried cleanly.");
                File.Delete(output);
                var wrongRange = new RangeTransport(bytes, false, true);
                var invalidRangeRejected = false;
                try { Download.Fetch(release, output, null, CancellationToken.None, new HttpClient(wrongRange)).GetAwaiter().GetResult(); }
                catch (InvalidDataException) { invalidRangeRejected = true; }
                Check(invalidRangeRejected && wrongRange.Calls == 1 && !File.Exists(output) && !File.Exists(output + ".partial"), "Wrong byte range was accepted or retried.");
                var multiBytes = new byte[33 * 1024 * 1024];
                multiBytes[0] = 17; multiBytes[multiBytes.Length - 1] = 53;
                using (var sha = SHA256.Create()) release.Sha256 = BitConverter.ToString(sha.ComputeHash(multiBytes)).Replace("-", "");
                release.Size = multiBytes.Length;
                var multi = new RangeTransport(multiBytes, false, false);
                Download.Fetch(release, output, null, CancellationToken.None, new HttpClient(multi)).GetAwaiter().GetResult();
                Check(multi.Calls == 2 && new FileInfo(output).Length == multiBytes.Length, "Multiple ranges did not reconstruct the full file.");
            }
            finally { Directory.Delete(root, true); }
        }

        private sealed class RangeTransport : HttpMessageHandler
        {
            private readonly byte[] source;
            private readonly bool interruptFirst, wrongRange;
            internal int Calls;
            internal RangeTransport(byte[] source, bool interruptFirst, bool wrongRange)
            { this.source = source; this.interruptFirst = interruptFirst; this.wrongRange = wrongRange; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Calls++;
                var requested = System.Linq.Enumerable.Single(request.Headers.Range.Ranges);
                var start = requested.From.Value;
                var end = requested.To.Value;
                var count = (int)(end - start + 1);
                if (interruptFirst && Calls == 1) count = Math.Max(0, count - 4);
                var body = new byte[count];
                Buffer.BlockCopy(source, (int)start, body, 0, count);
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { RequestMessage = request, Content = new ByteArrayContent(body) };
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(wrongRange ? start + 1 : start, end, source.Length);
                return Task.FromResult(response);
            }
        }
    }
}
