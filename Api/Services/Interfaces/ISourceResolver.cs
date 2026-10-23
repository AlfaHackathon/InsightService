using AlfaHackaton.Result;
using InsightService.Contracts.Context;

namespace InsightService.Services.Interfaces;

public interface ISourceResolver
{
    Task<Result<MaterialVersion>> GetAsync(string materialId, string versionId, CancellationToken cancellationToken);
}
