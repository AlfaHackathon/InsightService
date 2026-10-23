
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record QuestionIdentity(string Mechanism, string Frame, string Unit, string TargetPeriod, string? BaselinePeriod, string? Basis = null);
