namespace InsightService.Contracts.SourceReactions;

public sealed record ReactionPage(QuestionWork[] Items, string? NextCursor);
