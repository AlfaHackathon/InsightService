namespace InsightService.Contracts.Execution;

public sealed class SourceJobsResponse
{
    public JobFlowParameters[] Requests { get; set; } = [];
    public string? NextCursor { get; set; }
}

