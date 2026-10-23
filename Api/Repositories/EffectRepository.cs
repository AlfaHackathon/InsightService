using System.Net;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Models.Persistence;
using InsightService.Repositories.Interfaces;
using InsightService.Repositories.Postgres;
using InsightService.Resources;

namespace InsightService.Repositories;

public sealed class EffectRepository(IDapperContext context) : IEffectRepository
{
    public Task<Result<ReactionPage>> React(SourceChangeRequest change) => new SourceReactionRepository(context).React(change);
    public async Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null)
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        return await FindAnalysis(transaction, companyId, materialId, AnalysisProtocol.ProcessingVersion(versionId, trigger));
    }

    public async Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId) => (await GetSnapshot(companyId, materialId)).Effects;
    public async Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null)
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        var ids = await transaction.ListOrEmpty<string>(new QueryObject(InsightSql.FindSnapshotEffects, new
        {
            company = companyId.ToString("D"),
            material = materialId
        }));
        var effects = new List<EffectResponse>(ids.Count);
        foreach (var id in ids)
        {
            effects.Add((await EffectReader.ReadEffect(transaction, id, asOf))!);
        }

        var version = await StateVersion(transaction, companyId);
        transaction.Commit();
        return new EffectSnapshot(effects, version);
    }

    public async Task<EffectResponse?> GetEffect(string effectId)
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        var result = await EffectReader.ReadEffect(transaction, effectId);
        transaction.Commit();
        return result;
    }

    public async Task<Result<AnalysisReceipt>> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
    {
        using var transaction = await InsightTransaction.OpenAsync(context);
        var processingVersion = AnalysisProtocol.ProcessingVersion(request.NewsVersionId, request.Trigger);
        var existingReceipt = await FindAnalysis(transaction, request.CompanyId, request.NewsMaterialId, processingVersion);
        if (existingReceipt is not null)
        {
            if (existingReceipt.RequestHash != hash)
            {
                return new(new Error("This completed news analysis has a different payload.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }

            transaction.Commit();
            return new(AnalysisProtocol.Duplicate(existingReceipt.Receipt), null);
        }

        var completedAt = DateTimeOffset.UtcNow;
        if (request.ExpectedStateVersion is { } expected &&
            expected != await StateVersion(transaction, request.CompanyId))
        {
            return new(new Error("The immutable company snapshot changed before atomic commit.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        var saved = new List<SavedEffectResponse>(drafts.Count);
        foreach (var item in drafts)
        {
            var draftSave = await EffectRevisionWriter.SaveDraft(transaction, request, item, completedAt);
            if (draftSave.Error is { } draftSaveError)
            {
                return new(draftSaveError);
            }

            saved.Add(draftSave.Content!);
        }

        // Keep omitted heads in the receipt: later replay must not turn an unreviewed obligation into zero.
        var omittedIds = await transaction.ListOrEmpty<string>(new QueryObject(InsightSql.FindUnrevisedEffects,
            new
            {
                company = request.CompanyId.ToString("D"),
                material = request.NewsMaterialId,
                version = request.NewsVersionId,
                mechanisms = request.Mechanisms is null ? null : JsonSerializer.Serialize(request.Mechanisms)
            }));
        var savedIds = saved.Select(s => s.EffectId).ToHashSet(StringComparer.Ordinal);
        var unrevised = omittedIds.Where(id => !savedIds.Contains(id)).ToArray();
        var receipt = new AnalysisReceipt(PersistenceIdentity.StableId("analysis", request.CompanyId.ToString("D"), request.NewsMaterialId, processingVersion),
            request.CompanyId,
            request.BankProfileId,
            request.NewsMaterialId,
            request.NewsVersionId,
            request.AsOf,
            completedAt,
            false,
            AnalysisProtocol.ReceiptSemantics,
            saved,
            request.ContextComplete,
            unrevised,
            request.RetrievalTrace,
            request.Trigger);
        await transaction.Command(new QueryObject(InsightSql.InsertAnalysisReceipt,
            new
            {
                id = receipt.AnalysisId,
                company = request.CompanyId.ToString("D"),
                material = request.NewsMaterialId,
                version = processingVersion,
                hash,
                json = JsonSerializer.Serialize(receipt, AnalysisProtocol.Json)
            }));
        transaction.Commit();
        return new(receipt, null);
    }

    public async Task<Result<RecipientPage>> Recipients(DateTimeOffset asOf, string? after, string? materialId)
    {
        if (asOf == default ||
            after is not null &&
            !Guid.TryParse(after, out _))
        {
            return new(new Error("asOf and a valid company cursor are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        using var transaction = await InsightTransaction.OpenAsync(context);
        var ids = await transaction.ListOrEmpty<string>(new QueryObject(InsightSql.FindQuestionRecipients,
            new
            {
                after = after ?? "",
                asOf = asOf.UtcTicks,
                material = materialId
            }));
        return new(new RecipientPage(ids.Take(256).Select(Guid.Parse).ToArray(), ids.Count > 256 ? Guid.Parse(ids[255]).ToString("D") : null), null);
    }

    private static Task<long> StateVersion(ITransaction transaction, Guid company) => transaction.CommandWithResponse<long>(new QueryObject(InsightSql.ReadCompanyStateVersion, new { company = company.ToString("D") }));
    private static async Task<StoredAnalysis?> FindAnalysis(ITransaction transaction, Guid companyId, string materialId, string versionId)
    {
        var row = await transaction.FirstOrDefault<AnalysisRow>(new QueryObject(InsightSql.FindAnalysisReceipt,
            new
            {
                company = companyId.ToString("D"),
                material = materialId,
                version = versionId
            }));
        return row is null ? null : new StoredAnalysis(row.RequestHash, JsonSerializer.Deserialize<AnalysisReceipt>(row.PayloadJson, AnalysisProtocol.Json)!);
    }
}
