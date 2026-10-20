namespace Anime4KEncoder;

internal static class SegmentPlanning
{
    internal static int Seconds(int mode) => mode switch { 70 => 60, 80 => 180, 100 => 300, _ => throw new ArgumentOutOfRangeException(nameof(mode)) };

    internal static AnimeJanaiSegmentPlan Create(int index, long start, long total, long frames, int overlap)
    {
        if (index < 0 || start < 0 || start >= total || frames <= 0 || overlap < 0) throw new ArgumentOutOfRangeException(nameof(start));
        var end = start + Math.Min(frames, total - start);
        var sourceStart = Math.Max(0, start - overlap);
        var sourceEnd = end + Math.Min(overlap, total - end);
        return new(index, start, end, sourceStart, sourceEnd, start - sourceStart);
    }

    internal static AnimeJanaiSegmentPlan FromCheckpoint(SegmentCheckpoint checkpoint) =>
        new(checkpoint.Index, checkpoint.UsefulStartFrame, checkpoint.UsefulEndFrame,
            checkpoint.SourceStartFrame, checkpoint.SourceEndFrame, checkpoint.UsefulStartFrame - checkpoint.SourceStartFrame);

    // A stored prefix may end before the video: future blocks are planned only at their boundary.
    internal static bool ValidLayout(IReadOnlyList<SegmentCheckpoint>? checkpoints, long total, int overlap)
    {
        if (checkpoints is null || total <= 0 || overlap < 0) return false;
        long end = 0;
        for (var i = 0; i < checkpoints.Count; i++)
        {
            var c = checkpoints[i];
            if (c is null || c.Index != i || c.UsefulStartFrame != end || c.UsefulEndFrame <= end || c.UsefulEndFrame > total ||
                c.SourceStartFrame != Math.Max(0, end - overlap) ||
                c.SourceEndFrame != c.UsefulEndFrame + Math.Min(overlap, total - c.UsefulEndFrame)) return false;
            end = c.UsefulEndFrame;
        }
        return true;
    }

    internal static AnimeJanaiSegmentPlan SelectNext(List<SegmentCheckpoint> checkpoints, int index,
        long total, double fps, int mode, int overlap, int lastCachedIndex)
    {
        if (!double.IsFinite(fps) || fps <= 0 || index < 0 || index > checkpoints.Count || !ValidLayout(checkpoints, total, overlap))
            throw new InvalidOperationException("Plano de blocos inválido; cache preservado.");
        // Preserve every boundary through the last cached block, even if an earlier block needs repair.
        if (index <= lastCachedIndex)
        {
            if (index >= checkpoints.Count) throw new InvalidOperationException("Bloco em cache sem plano; cache preservado.");
            return FromCheckpoint(checkpoints[index]);
        }
        var start = index == 0 ? 0 : checkpoints[index - 1].UsefulEndFrame;
        var frames = checked((long)Math.Round(fps * Seconds(mode), MidpointRounding.AwayFromZero));
        var next = Create(index, start, total, Math.Max(1, frames), overlap);
        // Old fixed plans can have an unprocessed tail. Only this tail may be replaced.
        if (index < checkpoints.Count) checkpoints.RemoveRange(index, checkpoints.Count - index);
        checkpoints.Add(new SegmentCheckpoint
        {
            Index = next.Index, UsefulStartFrame = next.UsefulStartFrame, UsefulEndFrame = next.UsefulEndFrame,
            SourceStartFrame = next.SourceStartFrame, SourceEndFrame = next.SourceEndFrame
        });
        return next;
    }
}
