using InsightService.Contracts.Context;
using System.Text.Json.Serialization;

namespace InsightService.Contracts.Questions;

public sealed record CompanyApplicability(string Status, string Basis, string Explanation,
    SourceRef[] SourceRefs, string[] Assumptions);
