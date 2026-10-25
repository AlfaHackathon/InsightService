using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Reads;
using InsightService.Services.PublicReads;

internal sealed class PublicInsightReadFixture : IPublicInsightMembershipResolver, IPublicInsightSourceResolver, IPublicInsightReadStore
{
    internal static readonly Guid User = Guid.Parse("33333333-3333-4333-8333-333333333333");
    internal static readonly Guid Company = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid Foreign = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-25T00:00:00.0000001Z");
    internal static readonly string Hash = new('a', 64);
    internal static SourceRef Ref(MaterialVersion source) => new(source.MaterialId, source.VersionId, source.ContentHash);
    internal static MaterialVersion Material(string id, string kind = "company", Guid[]? companies = null) =>
        new(id, "v1", "synthetic", kind, id, "Synthetic loan L-123 exposure document", At, null, null,
            companies ?? [Company], [], Hash, new("uploaded", "https://example.test/original", At, "lead", null, "PRIVATE-LOCATOR"));
    internal static EffectRevision Revision() => new("revision-2", "revision-1", "v1", At, "Synthetic sourced consequence", "forecast",
        [new("news", "v1", Hash), new("own", "v1", Hash)],
        new(new ExpressionNode("value", "12345678901234567890.1234"), "RUB", "2026-11"), "12345678901234567890.1234", true, At,
        new("expense_delta", "contract_total", "L-123", "conditional", 4, 5, 3), "news",
        new("input_cost", "event_only", "RUB", "2026-11", null),
        new([], [], new("established", "documented_exposure", "Documented synthetic exposure", [new("own", "v1", Hash)], ["Conditional scenario"])),
        ModelExplanation: "PRIVATE-AUDIT", PublicationClass: "scenario",
        CompanyImportance: new(4, "Own company exposure requires review.", [new("own", "v1", Hash)]));

    internal PublicInsightMembership? Membership { get; set; } = new(Company, null);
    internal PersonalInsightRead? Personal { get; set; } = new("question-1", Company, "news", "company:" + Company.ToString("D"), Revision(), false);
    internal GeneralInsightRead? General { get; set; }
    internal List<PersonalInsightRead>? PersonalItems { get; set; }
    internal List<GeneralInsightRead>? GeneralItems { get; set; }
    internal List<EffectRevision>? Revisions { get; set; }
    internal Dictionary<(string, string), MaterialVersion> Materials { get; } = new();
    internal string? NextCursor { get; set; } = "opaque-next";
    internal int MembershipCalls, StoreCalls, SourceCalls, Active, MaximumActive;
    internal InsightReadOptions? Options;
    internal bool ThrowSource, MissingPage, ThrowCursor;
    internal TaskCompletionSource<bool>? SourceGate;
    internal TaskCompletionSource<bool> FourSourcesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal PublicInsightReadService Service() => new(this, this, this);
    internal PublicInsightReadFixture()
    {
        Materials[("news", "v1")] = Material("news", "news", []);
        Materials[("own", "v1")] = Material("own");
        General = GeneralItem();
    }
    internal static GeneralInsightRead GeneralItem(SourceRef[]? refs = null) => new("opaque-general", Company, "job-1", 23, "news", "v1", At.ToString("O"), At.ToString("O"), "lead", "completed",
        JsonSerializer.SerializeToElement(new
        {
            title = "Synthetic general result",
            conclusion = "Public source result",
            mechanism = "Input costs",
            applicability = new[] { "If exposed" },
            exclusions = Array.Empty<string>(),
            unknowns = new[] { "Company amount unknown" },
            sourceRefs = refs ?? [new("news", "v1", Hash)],
            rawCheckpoint = "PRIVATE-CHECKPOINT",
            companyImportance = new { score = 5 }
        }, AnalysisProtocol.Json));
    public Task<PublicInsightMembership?> Get(Guid userId, CancellationToken ct)
    {
        MembershipCalls++;
        return Task.FromResult(Membership);
    }
    public async Task<MaterialVersion?> Get(string materialId, string versionId, CancellationToken ct)
    {
        Interlocked.Increment(ref SourceCalls);
        var active = Interlocked.Increment(ref Active);
        int seen;
        do
        { seen = MaximumActive; } while (active > seen && Interlocked.CompareExchange(ref MaximumActive, active, seen) != seen);
        if (active == 4)
            FourSourcesStarted.TrySetResult(true);
        try
        {
            if (SourceGate is not null)
                await SourceGate.Task.WaitAsync(ct);
            if (ThrowSource)
                throw new HttpRequestException("PRIVATE upstream error");
            return Materials.GetValueOrDefault((materialId, versionId));
        }
        finally { Interlocked.Decrement(ref Active); }
    }
    public Task<InsightReadPage<PersonalInsightRead>> PersonalPage(Guid company, InsightReadOptions options, CancellationToken ct)
    {
        StoreCalls++;
        Options = options;
        if (ThrowCursor)
            throw new Microsoft.AspNetCore.Http.BadHttpRequestException("PRIVATE invalid cursor");
        return Task.FromResult(MissingPage ? null! : new InsightReadPage<PersonalInsightRead>(company, PersonalItems ?? (Personal is null ? [] : [Personal]), NextCursor));
    }
    public Task<PersonalInsightRead?> PersonalDetail(Guid company, string id, CancellationToken ct) { StoreCalls++; return Task.FromResult(Personal); }
    public Task<InsightReadPage<EffectRevision>?> PersonalHistory(Guid company, string id, int limit, string? cursor, CancellationToken ct)
    {
        StoreCalls++;
        return Task.FromResult<InsightReadPage<EffectRevision>?>(new(company, Revisions ?? [Personal!.CurrentRevision], NextCursor));
    }
    public Task<InsightReadPage<GeneralInsightRead>> GeneralPage(Guid company, InsightReadOptions options, CancellationToken ct)
    {
        StoreCalls++;
        Options = options;
        return Task.FromResult(new InsightReadPage<GeneralInsightRead>(company, GeneralItems ?? (General is null ? [] : [General]), NextCursor));
    }
    public Task<GeneralInsightRead?> GeneralDetail(Guid company, string id, CancellationToken ct) { StoreCalls++; return Task.FromResult(General); }
}
