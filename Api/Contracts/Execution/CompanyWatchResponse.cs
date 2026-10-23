namespace InsightService.Contracts.Execution;

public sealed class CompanyWatchResponse
{
    public JobFlowParameters[] Requests { get; set; } = [];
    public long AfterSequence { get; set; }
    public bool Complete { get; set; }
}
