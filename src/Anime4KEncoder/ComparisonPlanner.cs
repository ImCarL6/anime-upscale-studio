namespace Anime4KEncoder;

public sealed record ComparisonFramePlan(int SampleIndex, long OriginalFrame, long PilotFrame);
public sealed record PilotComparisonFrame(int SampleIndex, long OriginalFrame, long PilotFrame, string OriginalPath, string ProcessedPath);
public sealed record PilotComparisonResult(int Width, int Height, double FrameRate, string ProfileName, List<PilotComparisonFrame> Frames);

public static class ComparisonPlanner
{
    public static List<ComparisonFramePlan> Build(IEnumerable<(long StartFrame, long FrameCount)> samples)
    {
        var result = new List<ComparisonFramePlan>();
        long offset = 0;
        foreach (var sample in samples)
        {
            if (sample.StartFrame < 0 || sample.FrameCount <= 0) throw new ArgumentOutOfRangeException(nameof(samples));
            var middle = sample.FrameCount / 2;
            result.Add(new(result.Count, checked(sample.StartFrame + middle), checked(offset + middle)));
            offset = checked(offset + sample.FrameCount);
        }
        return result;
    }
}
