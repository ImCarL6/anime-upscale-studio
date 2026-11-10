using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AnimeUpscaleStudioInstaller
{
    internal sealed class Release
    {
        internal const string Repository = "ImCarL6/anime-upscale-studio";
        internal const string SetupName = "ImCarL6.AnimeUpscaleStudio-win-Setup.exe";
        internal string Version, DownloadUrl, Sha256;
        internal long Size;

        internal static Release Parse(string json)
        {
            var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            if (root == null || !root.ContainsKey("draft") || !root.ContainsKey("prerelease") ||
                Convert.ToBoolean(root["draft"]) || Convert.ToBoolean(root["prerelease"]))
                throw new InvalidDataException("A versão encontrada não é uma publicação estável.");
            var tag = Convert.ToString(root["tag_name"]);
            if (String.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("Versão não informada.");
            Release result = null;
            foreach (Dictionary<string, object> asset in (IEnumerable)root["assets"])
            {
                if (Convert.ToString(asset["name"]) != SetupName) continue;
                if (result != null) throw new InvalidDataException("Publicação com instaladores duplicados.");
                Uri url;
                var download = Convert.ToString(asset["browser_download_url"]);
                var expectedPath = "/" + Repository + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + SetupName;
                if (!Uri.TryCreate(download, UriKind.Absolute, out url) || url.Scheme != "https" ||
                    url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo != "" || url.Query != "" ||
                    url.Fragment != "" || !String.Equals(url.AbsolutePath, expectedPath, StringComparison.Ordinal))
                    throw new InvalidDataException("Endereço do instalador inválido.");
                var digest = asset.ContainsKey("digest") ? Convert.ToString(asset["digest"]) : "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(digest ?? "", "^sha256:[a-fA-F0-9]{64}$"))
                    throw new InvalidDataException("A publicação não informa a verificação de integridade do instalador.");
                var size = Convert.ToInt64(asset["size"]);
                if (size <= 0 || size > 4L * 1024 * 1024 * 1024 || Convert.ToString(asset["state"]) != "uploaded")
                    throw new InvalidDataException("O instalador ainda não está disponível por completo.");
                result = new Release { Version = tag, DownloadUrl = download, Sha256 = digest.Substring(7), Size = size };
            }
            if (result == null) throw new InvalidDataException("A última versão ainda não possui instalador para Windows.");
            return result;
        }
    }

    internal static class Download
    {
        internal static HttpClient Client()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(45) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AnimeUpscaleStudio-Installer/1.0");
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            client.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            return client;
        }

        internal static async Task<Release> Latest(CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var client = Client())
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                using (var response = await client.GetAsync("https://api.github.com/repos/" + Release.Repository + "/releases/latest", timeout.Token))
                {
                    response.EnsureSuccessStatusCode();
                    return Release.Parse(await response.Content.ReadAsStringAsync());
                }
            }
        }

        internal static async Task Fetch(Release release, string path, IProgress<int> progress, CancellationToken token)
        {
            using (var client = Client())
            using (var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage.RequestUri.Scheme != "https") throw new InvalidDataException("O download exige uma conexão segura.");
                using (var input = await response.Content.ReadAsStreamAsync())
                    await SaveVerified(input, path, release, progress, token);
            }
        }

        internal static async Task SaveVerified(Stream input, string path, Release release, IProgress<int> progress, CancellationToken token)
        {
            var partial = path + ".partial";
            var created = false;
            try
            {
                var outputFile = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                created = true;
                using (var output = outputFile)
                using (var hash = SHA256.Create())
                {
                    var buffer = new byte[1024 * 1024];
                    long total = 0;
                    var previous = -1;
                    int count;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token)) > 0)
                    {
                        total += count;
                        if (total > release.Size) throw new InvalidDataException("O download tem um tamanho diferente do publicado.");
                        hash.TransformBlock(buffer, 0, count, buffer, 0);
                        await output.WriteAsync(buffer, 0, count, token);
                        var percent = (int)(100 * total / release.Size);
                        if (percent != previous && progress != null) { progress.Report(percent); previous = percent; }
                    }
                    token.ThrowIfCancellationRequested();
                    hash.TransformFinalBlock(new byte[0], 0, 0);
                    var actual = BitConverter.ToString(hash.Hash).Replace("-", "");
                    if (total != release.Size || !String.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("O arquivo baixado não passou na verificação de integridade. Tente novamente.");
                }
                token.ThrowIfCancellationRequested();
                File.Move(partial, path);
            }
            finally { if (created && File.Exists(partial)) File.Delete(partial); }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly Label status = new Label { AutoSize = false, Location = new Point(26, 78), Size = new Size(470, 74) };
        private readonly ProgressBar progress = new ProgressBar { Location = new Point(26, 158), Size = new Size(470, 18) };
        private readonly Button install = new Button { Text = "Instalar", Location = new Point(270, 205), Size = new Size(110, 38), Enabled = false };
        private readonly Button cancel = new Button { Text = "Cancelar", Location = new Point(388, 205), Size = new Size(108, 38) };
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool busy, installing, closeRequested;
        private string directory;

        internal InstallerForm()
        {
            Text = "Instalar Anime Upscale Studio";
            ClientSize = new Size(525, 270);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(18, 24, 36);
            ForeColor = Color.WhiteSmoke;
            Controls.Add(new Label { Text = "Anime Upscale Studio", Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, Location = new Point(22, 24) });
            Controls.AddRange(new Control[] { status, progress, install, cancel });
            install.BackColor = Color.FromArgb(117, 91, 230);
            install.FlatStyle = cancel.FlatStyle = FlatStyle.Flat;
            cancel.BackColor = Color.FromArgb(33, 43, 61);
            install.Click += async delegate { await Install(); };
            cancel.Click += delegate { Close(); };
            Shown += async delegate { await Check(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; if (!installing) { closeRequested = true; cancellation.Cancel(); } }
            };
        }

        private async Task Check()
        {
            busy = true;
            status.Text = "Buscando a versão mais recente…";
            progress.Style = ProgressBarStyle.Marquee;
            try
            {
                var latest = await Download.Latest(cancellation.Token);
                status.Text = latest.Version + " disponível • " + (latest.Size / (1024d * 1024)).ToString("N0") + " MB\nO instalador consulta a versão atual a cada execução.";
            }
            catch (Exception ex) { Error(ex); }
            finally { Finish(); }
        }

        private async Task Install()
        {
            busy = true;
            install.Enabled = false;
            try
            {
                if (Process.GetProcessesByName("Anime4KEncoder").Length > 0)
                    throw new InvalidOperationException("Feche o Anime Upscale Studio para instalar. Se houver processamento em andamento, cancele e aguarde sua finalização primeiro.");
                status.Text = "Confirmando a versão mais recente…";
                var latest = await Download.Latest(cancellation.Token);
                directory = Path.Combine(Path.GetTempPath(), "AnimeUpscaleStudioInstaller-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var setup = Path.Combine(directory, Release.SetupName);
                progress.Style = ProgressBarStyle.Continuous;
                progress.Value = 0;
                status.Text = "Baixando " + latest.Version + "…\nA integridade será conferida antes da instalação.";
                await Download.Fetch(latest, setup, new Progress<int>(value => progress.Value = Math.Max(0, Math.Min(100, value))), cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (Process.GetProcessesByName("Anime4KEncoder").Length > 0)
                    throw new InvalidOperationException("O aplicativo foi aberto durante o download. Feche-o antes de tentar instalar novamente.");
                installing = true;
                cancel.Enabled = false;
                status.Text = "Instalando " + latest.Version + "…\nAguarde a conclusão na janela do instalador.";
                using (var process = Process.Start(new ProcessStartInfo(setup) { UseShellExecute = true, WorkingDirectory = directory }))
                {
                    if (process == null) throw new IOException("Não foi possível abrir o instalador.");
                    await Task.Run(delegate { process.WaitForExit(); });
                    if (process.ExitCode != 0) throw new IOException("A instalação não foi concluída. Você pode tentar novamente.");
                }
                status.Text = latest.Version + " instalada com sucesso.";
                install.Visible = false;
                cancel.Text = "Fechar";
            }
            catch (Exception ex) { Error(ex); }
            finally
            {
                installing = false;
                cancel.Enabled = true;
                if (directory != null) { try { Directory.Delete(directory, true); } catch { } directory = null; }
                Finish();
            }
        }

        private void Error(Exception ex)
        {
            if (closeRequested) return;
            status.Text = ex is HttpRequestException || ex is TaskCanceledException
                ? "Não foi possível buscar a versão atual. Confira sua conexão e tente novamente."
                : ex.Message;
            install.Text = "Tentar novamente";
            install.Width = 155;
            install.Left = 225;
        }

        private void Finish()
        {
            busy = false;
            progress.Style = ProgressBarStyle.Continuous;
            install.Enabled = true;
            if (closeRequested) Close();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 2 && (args[0] == "--check-latest" || args[0] == "--download-latest"))
                {
                    var latest = Download.Latest(CancellationToken.None).GetAwaiter().GetResult();
                    if (args[0] == "--download-latest")
                    {
                        var temporary = Path.Combine(Path.GetTempPath(), "AnimeUpscaleStudioInstaller-test-" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(temporary);
                        try { Download.Fetch(latest, Path.Combine(temporary, Release.SetupName), null, CancellationToken.None).GetAwaiter().GetResult(); }
                        finally { Directory.Delete(temporary, true); }
                    }
                    File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new { Version = latest.Version, Url = latest.DownloadUrl, Sha256 = latest.Sha256, Size = latest.Size, Installed = false }), Encoding.UTF8);
                    return 0;
                }
                if (args.Length == 2 && args[0] == "--self-test") { SelfTests.Run(); File.WriteAllText(args[1], "LATEST_INSTALLER=PASS", Encoding.UTF8); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (args.Length == 2) File.WriteAllText(args[1], ex.ToString(), Encoding.UTF8);
                else MessageBox.Show(ex.Message, "Anime Upscale Studio");
                return 1;
            }
        }
    }
}
