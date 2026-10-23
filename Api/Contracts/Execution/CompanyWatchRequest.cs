namespace InsightService.Contracts.Execution;

public sealed class CompanyWatchRequest
{
    public string JobId { get; set; } = string.Empty;
    public Guid CompanyId { get; set; }
    public long AfterSequence { get; set; }
}
