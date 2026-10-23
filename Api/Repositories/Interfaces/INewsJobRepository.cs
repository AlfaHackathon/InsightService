using InsightService.Contracts.NewsJobs;
namespace InsightService.Repositories.Interfaces;

public interface INewsJobRepository
{
    Task<NewsJob> Create(NewsJobRequest request);
    Task<NewsJob> Get(string id);
    Task<NewsJob?> Claim(JobClaim claim);
    Task<NewsJob> Update(string id, JobUpdate update);
    Task<NewsJob> Retry(string id);
    Task<SourceCursor> GetSourceCursor();
    Task<SourceCursor> SaveSourceCursor(SourceCursor cursor);
}
