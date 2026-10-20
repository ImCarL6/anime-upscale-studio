using System.IO;
using System.Text.Json;

namespace Anime4KEncoder;

internal sealed class QueueSession
{
    public int Version { get; set; } = 1;
    public string OutputDirectory { get; set; } = "";
    public string InputDirectory { get; set; } = "";
    public double ElapsedSeconds { get; set; }
    public string Engine { get; set; } = "";
    public string Profile { get; set; } = "";
    public int? Cq { get; set; }
    public bool? AqStrength10 { get; set; }
    public bool? BAdapt { get; set; }
    public bool AdvancedMode { get; set; }
    public string AdvancedCommand { get; set; } = "";
    public List<QueueEntry> Items { get; set; } = [];
}

internal sealed class QueueEntry
{
    public string InputPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Status { get; set; } = "Aguardando";
    public string Error { get; set; } = "";
    public string LogPath { get; set; } = "";
    public MediaInfo? Media { get; set; }
    public double Progress { get; set; }
    public double ElapsedSeconds { get; set; }
    public long OutputBytes { get; set; }
}

internal static class SessionStore
{
    internal static QueueSession? Load(string path)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                var state = JsonSerializer.Deserialize<QueueSession>(File.ReadAllText(candidate));
                if (state?.Version == 1 && state.Items is not null && state.Items.All(entry => entry is not null &&
                    double.IsFinite(entry.ElapsedSeconds) && entry.ElapsedSeconds >= 0 && entry.ElapsedSeconds < TimeSpan.MaxValue.TotalSeconds - 1) &&
                    double.IsFinite(state.ElapsedSeconds) && state.ElapsedSeconds >= 0 && state.ElapsedSeconds < TimeSpan.MaxValue.TotalSeconds - 1)
                    return state;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    internal static void Save(string path, QueueSession state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".writing";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }
}
