using AlfaHackaton.Result;
using InsightService.Contracts.NewsJobs;

namespace InsightService.Repositories.Interfaces;

public interface INewsJobRepository
{
    Task<Result<NewsJob>> Create(NewsJobRequest request);
    Task<Result<NewsJob>> GetAsync(string id);
    Task<Result<NewsJob?>> Claim(JobClaim claim);
    Task<Result<NewsJob>> Update(string id, JobUpdate update);
    Task<Result<NewsJob>> Retry(string id);
    Task<SourceCursor> GetSourceCursor(string source = "context");
    Task<Result<SourceCursor>> SaveSourceCursor(SourceCursor cursor, string source = "context");
}
