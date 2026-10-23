
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record RecipientPage(Guid[] CompanyIds, string? NextCursor);
