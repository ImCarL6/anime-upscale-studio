using System.IO;

namespace Anime4KEncoder;

internal static class JobTemporaryFiles
{
    // Call only after final validation and promotion of the output file.
    internal static string? Cleanup(string workDirectory, bool outputCompleted)
    {
        if (!outputCompleted) return null;
        try
        {
            var job = Path.GetFullPath(workDirectory);
            var parent = Path.GetDirectoryName(job);
            if (parent is null || !string.Equals(Path.GetFileName(parent), ".anime-upscale-temp", StringComparison.OrdinalIgnoreCase))
                return "A pasta não é um diretório temporário de trabalho.";
            if (Directory.Exists(job)) Directory.Delete(job, recursive: true);
            // Other jobs, including failed or cancelled jobs, must survive.
            if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
