using System.ComponentModel.DataAnnotations;

namespace InsightService.Contracts.PublicReads;

public sealed record PublicInsightMembership(Guid CompanyId, string? BankProfileId);

public sealed class PublicInsightFeedQuery
{
    public string Kind { get; set; } = "personal";
    [MinLength(1), RegularExpression("^(risk|opportunity|event)$")] public string? Category { get; set; }
    [Range(1, 20)] public int Limit { get; set; } = 5;
    [MaxLength(2048)] public string? Cursor { get; set; }
    [MinLength(1), MaxLength(100)] public string? Status { get; set; }
    public string? Mechanism { get; set; }
    [MinLength(1), MaxLength(200)] public string? NewsMaterialId { get; set; }
    [Range(1, 5)] public int? MinImportance { get; set; }
}

public sealed class PublicInsightHistoryQuery
{
    [Range(1, 20)] public int Limit { get; set; } = 10;
    [MaxLength(2048)] public string? Cursor { get; set; }
}
