namespace InsightService.Contracts.NewsJobs;

public sealed record JobClaim(string WorkerId, string? JobId = null);
