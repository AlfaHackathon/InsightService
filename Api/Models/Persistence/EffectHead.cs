namespace InsightService.Models.Persistence;

internal sealed record EffectHead(
    string EffectKey,
    Guid CompanyId,
    string? BankProfileId,
    string NewsMaterialId,
    string LiteralObjectRef,
    string CurrentRevisionId);
