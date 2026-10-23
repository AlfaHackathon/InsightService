using InsightService.Contracts.Analyses;

namespace InsightService.Models.Persistence;

public sealed record StoredAnalysis(string RequestHash, AnalysisReceipt Receipt);
