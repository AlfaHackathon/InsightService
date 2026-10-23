using InsightService.Contracts.Execution;
using InsightService.Contracts.NewsJobs;

namespace InsightService.Models;

public sealed class ExecutionJobContext
{
    public required NewsJob Job { get; set; }
    public required JobFlowParameters Request { get; set; }
    public required JobExecutionCheckpoint Checkpoint { get; set; }
}
