using System.IO;
using System.Security.Cryptography;

namespace Anime4KEncoder;

internal static class StoragePaths
{
    internal static string DataRoot(string applicationRoot) =>
        File.Exists(Path.Combine(applicationRoot, "sq.version"))
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeUpscaleStudio", "UserData")
            : applicationRoot;

    internal static string ModelRoot(string applicationRoot, string dataRoot)
    {
        var bundled = Path.Combine(applicationRoot, "2x_AnimeJaNai_HD_V3_ModelsOnly");
        if (string.Equals(applicationRoot, dataRoot, StringComparison.OrdinalIgnoreCase)) return bundled;
        var manifest = Path.Combine(applicationRoot, "runtime-manifest.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException("Pacote incompleto: runtime-manifest.json ausente.", manifest);
        // Only runtime/model changes invalidate engines, not application-only updates.
        var key = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest)));
        var destination = Path.Combine(dataRoot, "models", key);
        Directory.CreateDirectory(destination);
        if (Directory.Exists(bundled))
            foreach (var source in Directory.EnumerateFiles(bundled, "*.onnx"))
            {
                var target = Path.Combine(destination, Path.GetFileName(source));
                if (File.Exists(target)) continue;
                var temporary = target + ".copying";
                File.Copy(source, temporary, overwrite: true);
                File.Move(temporary, target);
            }
        return destination;
    }
}
