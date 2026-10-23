namespace InsightService.Contracts.Projections;

public sealed class CompanyIdentity
{
    public Guid CompanyId { get; set; }
    public DateTimeOffset KnownAt { get; set; }
    public string? BankProfileId { get; set; }
}
