namespace Anime4KEncoder;

public sealed record AnimeJanaiSegmentPlan(
    int Index,
    long UsefulStartFrame,
    long UsefulEndFrame,
    long SourceStartFrame,
    long SourceEndFrame,
    long PrefixOverlapFrames)
{
    public long UsefulFrameCount => UsefulEndFrame - UsefulStartFrame;
    public long SourceFrameCount => SourceEndFrame - SourceStartFrame;
}
public sealed class SegmentManifest
{
    public int Version { get; set; }
    public string SourcePath { get; set; } = "";
    public long SourceLength { get; set; }
    public long SourceLastWriteUtcTicks { get; set; }
    public long TotalFrames { get; set; }
    public double FrameRate { get; set; }
    public string ProfileCode { get; set; } = "";
    public string ModelName { get; set; } = "";
    public int Cq { get; set; }
    public bool IsBeta { get; set; }
    public string BetaSignature { get; set; } = "";
    public string ToolchainFingerprint { get; set; } = "";
    public int SegmentSeconds { get; set; }
    public int LayoutPolicyVersion { get; set; }
    public int OverlapFrames { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<SegmentCheckpoint> Segments { get; set; } = [];
}
public sealed class SegmentCheckpoint
{
    public int Index { get; set; }
    public long UsefulStartFrame { get; set; }
    public long UsefulEndFrame { get; set; }
    public long SourceStartFrame { get; set; }
    public long SourceEndFrame { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public long OutputBytes { get; set; }
}

