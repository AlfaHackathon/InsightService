using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
namespace InsightService.Services.Interfaces;

public interface IAnalysisService
{
    Task<AnalysisReceipt> Save(AnalysisRequest request, CancellationToken ct = default);
    Task<AnalysisReceipt> GetAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null);
    Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null);
    Task<EffectResponse> GetEffect(string effectId);
    Task<RecipientPage> Recipients(DateTimeOffset asOf, string? after, string? materialId);
    Task<ReactionPage> React(SourceChangeRequest change);
}
