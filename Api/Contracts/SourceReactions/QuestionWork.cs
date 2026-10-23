namespace InsightService.Contracts.SourceReactions;

public sealed record QuestionWork(string? EffectId, Guid CompanyId, string? BankProfileId, string NewsMaterialId, string NewsVersionId, string Kind = "question", string? AnalysisId = null);
