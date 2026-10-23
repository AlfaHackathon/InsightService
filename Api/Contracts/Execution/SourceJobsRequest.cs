namespace InsightService.Contracts.Execution;

public sealed class SourceJobsRequest
{
    public SourceEvent Event { get; set; } = new();
    public string? AfterQuestion { get; set; }
}

