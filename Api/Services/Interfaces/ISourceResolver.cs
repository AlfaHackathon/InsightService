using InsightService.Contracts.Context;
namespace InsightService.Services.Interfaces;

public interface ISourceResolver
{
    Task<MaterialVersion> GetAsync(string materialId, string versionId, CancellationToken cancellationToken);
}
