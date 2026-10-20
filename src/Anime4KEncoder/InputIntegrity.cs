using System.Globalization;

namespace Anime4KEncoder;

internal sealed class InputTimeline
{
    internal double AudioVideoEnd { get; private set; }
    internal double SubtitleEnd { get; private set; }
    internal long VideoPackets { get; private set; }
    internal void Add(string line)
    {
        string type = "";
        double pts = double.NaN, duration = 0;
        foreach (var part in line.Split('|'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) continue;
            if (pair[0] == "codec_type") type = pair[1];
            if (pair[0] == "pts_time") double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out pts);
            if (pair[0] == "duration_time") double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }
        if (type == "video") VideoPackets++;
        if (!double.IsFinite(pts)) return;
        if (type is "video" or "audio") AudioVideoEnd = Math.Max(AudioVideoEnd, pts + duration);
        if (type == "subtitle") SubtitleEnd = Math.Max(SubtitleEnd, pts + duration);
    }

    internal void Validate(double declaredSeconds)
    {
        if (VideoPackets <= 0 || AudioVideoEnd <= 0)
            throw new InvalidOperationException("A entrada não contém vídeo com uma linha de tempo válida.");
        if (declaredSeconds - AudioVideoEnd > 3 && Math.Abs(declaredSeconds - SubtitleEnd) > 3)
            throw new InvalidOperationException($"Entrada possivelmente incompleta: o MKV anuncia {TimeSpan.FromSeconds(declaredSeconds):hh\\:mm\\:ss}, mas os dados terminam em {TimeSpan.FromSeconds(AudioVideoEnd):hh\\:mm\\:ss}. Verifique a cópia original.");
    }

    internal static void ValidateReader(int exitCode, string errors)
    {
        if (exitCode == 0 && string.IsNullOrWhiteSpace(errors)) return;
        if (errors.Contains("File ended prematurely", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Arquivo incompleto: o leitor encontrou um final prematuro. Obtenha uma cópia íntegra antes do upscale.");
        throw new InvalidOperationException("Falha de integridade na mídia: " + (string.IsNullOrWhiteSpace(errors) ? $"código {exitCode}" : errors.Trim()[..Math.Min(2000, errors.Trim().Length)]));
    }
}
