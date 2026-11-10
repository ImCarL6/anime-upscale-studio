namespace Anime4KEncoder;

public sealed record QueueEstimateInput(
    long InputBytes, double DurationSeconds, int InputWidth, int InputHeight,
    long InputBitRate, int AudioTracks, bool HasAssSubtitle,
    string InputVideoCodec, string InputPixelFormat);

public static class QueueEstimates
{
    public static AdaptiveEstimate Predict(QueueEstimateInput item, IReadOnlyList<HistoryRecord> history,
        string engine, string profile, int cq, int concurrency, bool isBeta,
        string betaSignature, int pipelineVersion, int resourcePercent, DateTimeOffset now)
    {
        var candidates = history
            .Where(h => h.ResourcePercent == resourcePercent && h.Engine == engine && h.IsBeta == isBeta && h.BetaSignature == betaSignature &&
                        (engine != "animejanai" || h.PipelineVersion == pipelineVersion) &&
                        double.IsFinite(h.DurationSeconds) && h.DurationSeconds > 0 &&
                        double.IsFinite(h.ElapsedSeconds) && h.ElapsedSeconds > 0 && h.OutputBytes > 0)
            .Select(h => new { Record = h, Distance = EstimateDistance(h, item, profile, cq, concurrency) })
            .OrderBy(x => x.Distance)
            .ThenByDescending(x => x.Record.Timestamp)
            .Take(80)
            .ToArray();

        var weighted = candidates
            .Select(x =>
            {
                var ageDays = Math.Max(0, (now - x.Record.Timestamp).TotalDays);
                var recency = 1.0 / (1.0 + ageDays / 90.0);
                var weight = Math.Exp(-x.Distance) * recency;
                return (x.Record, Weight: weight);
            })
            .Where(x => x.Weight > 0.02)
            .ToArray();

        var weightTotal = weighted.Sum(x => x.Weight);
        // The initial guess has one sample of weight. Real measurements gradually replace it.
        var learnedFactor = weightTotal / (1.0 + weightTotal);
        var defaultRatio = DefaultSizeRatio(engine) * Math.Pow(2, (28 - cq) / 6.0);
        var defaultOutput = Math.Max(0, item.InputBytes * Math.Clamp(defaultRatio, 0.35, 12));
        var defaultTimeFactor = DefaultRealTimeFactor(engine, profile);
        var defaultTime = Math.Max(1, item.DurationSeconds * defaultTimeFactor);

        if (weightTotal <= 0 || item.DurationSeconds <= 0)
            return new AdaptiveEstimate((long)defaultOutput, defaultTime, 0, 0, "estimativa padrão");

        var learnedOutputRate = weighted.Sum(x => x.Record.OutputBytes / x.Record.DurationSeconds * x.Weight) / weightTotal;
        var learnedTimeFactor = weighted.Sum(x => x.Record.ElapsedSeconds / x.Record.DurationSeconds * x.Weight) / weightTotal;
        var learnedOutput = Math.Max(0, learnedOutputRate * item.DurationSeconds);
        var learnedTime = Math.Max(1, learnedTimeFactor * item.DurationSeconds);

        var output = (long)Math.Clamp(defaultOutput * (1 - learnedFactor) + learnedOutput * learnedFactor, 1, long.MaxValue);
        var time = Math.Max(1, defaultTime * (1 - learnedFactor) + learnedTime * learnedFactor);
        var confidence = Math.Clamp(learnedFactor * Math.Min(1, weighted.Length / 5.0), 0, 1);
        var exactSamples = weighted.Count(x => x.Record.Profile == profile && Math.Abs(x.Record.Cq - cq) <= 3);
        var basis = exactSamples > 0 ? $"{exactSamples} execuções semelhantes" : $"{weighted.Length} execuções do motor";
        return new AdaptiveEstimate(output, time, weighted.Length, confidence, basis);
    }

    private static double EstimateDistance(HistoryRecord record, QueueEstimateInput item, string profile, int cq, int concurrency)
    {
        var distance = record.Profile == profile ? 0 : 1.35;
        distance += Math.Abs(record.Cq - cq) / 6.0;
        if (record.Concurrency > 0) distance += Math.Abs(record.Concurrency - concurrency) * 0.35;

        var itemPixels = Math.Max(1, item.InputWidth * item.InputHeight);
        var recordPixels = Math.Max(1, record.InputWidth * record.InputHeight);
        distance += Math.Abs(Math.Log((double)itemPixels / recordPixels, 2)) * 0.55;

        var itemSourceRate = item.InputBitRate > 0
            ? item.InputBitRate
            : item.DurationSeconds > 0 ? item.InputBytes * 8.0 / item.DurationSeconds : 0;
        var recordSourceRate = record.InputBitRate > 0
            ? record.InputBitRate
            : record.DurationSeconds > 0 ? record.InputBytes * 8.0 / record.DurationSeconds : 0;
        if (itemSourceRate > 0 && recordSourceRate > 0)
            distance += Math.Abs(Math.Log(itemSourceRate / recordSourceRate, 2)) * 0.45;

        if (item.AudioTracks > 0 && record.AudioTracks > 0)
            distance += Math.Abs(item.AudioTracks - record.AudioTracks) * 0.16;
        if (item.HasAssSubtitle != record.HasAssSubtitle) distance += 0.18;
        if (!string.IsNullOrWhiteSpace(item.InputVideoCodec) && !string.IsNullOrWhiteSpace(record.InputVideoCodec) &&
            !string.Equals(item.InputVideoCodec, record.InputVideoCodec, StringComparison.OrdinalIgnoreCase)) distance += 0.16;
        if (!string.IsNullOrWhiteSpace(item.InputPixelFormat) && !string.IsNullOrWhiteSpace(record.InputPixelFormat))
        {
            var itemIs10Bit = item.InputPixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) || item.InputPixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
            var recordIs10Bit = record.InputPixelFormat.Contains("10", StringComparison.OrdinalIgnoreCase) || record.InputPixelFormat.Contains("p010", StringComparison.OrdinalIgnoreCase);
            if (itemIs10Bit != recordIs10Bit) distance += 0.12;
        }
        return distance;
    }

    private static double DefaultSizeRatio(string engine) => engine == "animejanai" ? 2.2 : 1.8;
    private static double DefaultRealTimeFactor(string engine, string profile) => engine == "anime4k"
        ? 0.75
        : profile.Contains("Performance", StringComparison.OrdinalIgnoreCase) ? 2.5 : 4.5;

}

public sealed record HistoryRecord(
    DateTimeOffset Timestamp,
    string Engine,
    string Profile,
    int Cq,
    long InputBytes,
    long OutputBytes,
    double DurationSeconds,
    double ElapsedSeconds,
    int InputWidth = 1920,
    int InputHeight = 1080,
    long InputBitRate = 0,
    int AudioTracks = 0,
    bool HasAssSubtitle = false,
    string InputVideoCodec = "",
    string InputPixelFormat = "",
    int Concurrency = 1,
    bool IsBeta = false,
    string BetaSignature = "",
    int PipelineVersion = 0,
    int SegmentSeconds = 0)
{
    public int ResourcePercent { get; init; } = 100;
}
public sealed record AdaptiveEstimate(long OutputBytes, double TotalSeconds, int SampleCount, double Confidence, string Basis);
