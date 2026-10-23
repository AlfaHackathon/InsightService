using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;

namespace InsightService.Repositories.Interfaces;

public interface IEffectRepository
{
    Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null);
    Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId);
    Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null);
    Task<EffectResponse?> GetEffect(string effectId);
    Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId);
    Task<Result<ReactionPage>> React(SourceChangeRequest change);
    Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts);
}
