using System.ComponentModel.DataAnnotations;
namespace InsightService.Contracts.Reads;

public sealed class InsightReadOptions
{
    [Range(1, 20)] public int Limit { get; set; } = 5;
    [MaxLength(2048)] public string? Cursor { get; set; }
    [MaxLength(100)] public string? Status { get; set; }
    [MaxLength(100)] public string? Mechanism { get; set; }
    [MaxLength(200)] public string? NewsMaterialId { get; set; }
    [Range(1, 5)] public int? MinImportance { get; set; }
}
