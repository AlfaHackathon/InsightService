namespace InsightService.Contracts.Analyses;

public sealed class DepthSource
{
    public string MaterialId { get; set; } = string.Empty;
    public string VersionId { get; set; } = string.Empty;
    public string ContentHash { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public DateTimeOffset KnownAt { get; set; }
}

