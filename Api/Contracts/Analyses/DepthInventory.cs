namespace InsightService.Contracts.Analyses;

public sealed class DepthInventory
{
    public Guid CompanyId { get; set; }
    public DateTimeOffset AsOf { get; set; }
    public bool CompleteForLoadedScope { get; set; }
    public DepthSource[] Sources { get; set; } = [];
    public long WatchAfterSequence { get; set; }
}
