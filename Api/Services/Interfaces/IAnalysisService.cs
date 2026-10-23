using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;

namespace InsightService.Services.Interfaces;

public interface IAnalysisService
{
    Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, CancellationToken ct = default);
    Task<Result<AnalysisReceipt>> GetAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null);
    Task<Result<EffectSnapshot>> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null);
    Task<Result<EffectResponse>> GetEffect(string effectId);
    Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId);
    Task<Result<ReactionPage>> React(SourceChangeRequest change);
}
