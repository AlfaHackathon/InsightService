using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Contracts.SourceReactions;
using InsightService.Migrations;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Repositories.Interfaces;
using InsightService.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InsightService.Repositories.Postgres;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;

namespace InsightService.Repositories;

public sealed class EffectRepository : IEffectRepository
{
    private readonly IDapperContext context;
    public EffectRepository(IDapperContext context) => this.context = context;

    public async Task<ReactionPage> React(SourceChangeRequest change) => await new SourceReactionRepository(Open).React(change);

    public async Task<StoredAnalysis?> FindAnalysis(Guid companyId, string materialId, string versionId, MaterialTrigger? trigger = null)
    {
        using var connection = await Open();
        return await FindAnalysis(connection, companyId, materialId, AnalysisProtocol.ProcessingVersion(versionId, trigger));
    }

    public async Task<IReadOnlyList<EffectResponse>> GetEffects(Guid companyId, string materialId)
        => (await GetSnapshot(companyId, materialId)).Effects;

    public async Task<EffectSnapshot> GetSnapshot(Guid companyId, string materialId, DateTimeOffset? asOf = null)
    {
        using var connection = await Open();

        var command = new QueryObject(InsightSql.FindSnapshotEffects);
        command.WithParameter("@company", companyId.ToString("D"));
        command.WithParameter("@material", materialId);
        var ids = new List<string>();
        using (var reader = await connection.Rows(command)) while (reader.Read()) ids.Add(reader.GetString(0));
        var effects = new List<EffectResponse>();
        foreach (var id in ids) effects.Add((await EffectReader.ReadEffect(connection, id, asOf))!);
        var version = await StateVersion(connection, companyId);
        connection.Commit();
        return new(effects, version);
    }

    public async Task<EffectResponse?> GetEffect(string effectId)
    {
        using var connection = await Open();

        var result = await EffectReader.ReadEffect(connection, effectId);
        connection.Commit();
        return result;
    }

    public async Task<AnalysisReceipt> Save(AnalysisRequest request, string hash, IReadOnlyList<PreparedDraft> drafts)
    {
        using var connection = await Open();

        var processingVersion = AnalysisProtocol.ProcessingVersion(request.NewsVersionId, request.Trigger);
        var existingReceipt = await FindAnalysis(connection, request.CompanyId, request.NewsMaterialId, processingVersion);
        if (existingReceipt is not null)
        {
            if (existingReceipt.RequestHash != hash)
                throw new ApiException(409, "analysis_payload_conflict", "This completed news analysis has a different payload.");
            connection.Commit();
            return AnalysisProtocol.Duplicate(existingReceipt.Receipt);
        }
        var completedAt = DateTimeOffset.UtcNow;
        if (request.ExpectedStateVersion is { } expected && expected != await StateVersion(connection, request.CompanyId))
            throw new ApiException(409, "stale_company_snapshot", "The immutable company snapshot changed before atomic commit.");
        var saved = new List<SavedEffectResponse>();
        foreach (var item in drafts)
            saved.Add(await EffectRevisionWriter.SaveDraft(connection, request, item, completedAt));
        // Omitted drafts preserve their actual heads. Record the gap in this immutable receipt,
        // so replay or a later head cannot turn an unreviewed obligation into a zero group total.
        var unrevised = new List<string>();
        {

            var omitted = new QueryObject(InsightSql.FindUnrevisedEffects);
            omitted.WithParameter("@company", request.CompanyId.ToString("D"));
            omitted.WithParameter("@material", request.NewsMaterialId);
            omitted.WithParameter("@version", request.NewsVersionId);
            omitted.WithParameter("@mechanisms", request.Mechanisms is null ? DBNull.Value : JsonSerializer.Serialize(request.Mechanisms));
            using var reader = await connection.Rows(omitted);
            var savedIds = saved.Select(s => s.EffectId).ToHashSet(StringComparer.Ordinal);
            while (reader.Read()) if (!savedIds.Contains(reader.GetString(0))) unrevised.Add(reader.GetString(0));
        }
        var receipt = new AnalysisReceipt(PersistenceIdentity.StableId("analysis", request.CompanyId.ToString("D"),
            request.NewsMaterialId, processingVersion), request.CompanyId, request.BankProfileId,
            request.NewsMaterialId, request.NewsVersionId, request.AsOf, completedAt, false,
            AnalysisProtocol.ReceiptSemantics, saved, request.ContextComplete, unrevised, request.RetrievalTrace, request.Trigger);
        {

            var insert = new QueryObject(InsightSql.InsertAnalysisReceipt);
            insert.WithParameter("@id", receipt.AnalysisId);
            insert.WithParameter("@company", request.CompanyId.ToString("D"));
            insert.WithParameter("@material", request.NewsMaterialId);
            insert.WithParameter("@version", processingVersion);
            insert.WithParameter("@hash", hash);
            insert.WithParameter("@json", JsonSerializer.Serialize(receipt, AnalysisProtocol.Json));
            await connection.Execute(insert);
        }
        connection.Commit();
        return receipt;
    }

    private Task<PgSession> Open() => PgSession.Open(context);
    private static async Task<long> StateVersion(PgSession connection, Guid company)
    {
        var c = new QueryObject(InsightSql.ReadCompanyStateVersion);
        c.WithParameter("@company", company.ToString("D")); return (long)(await connection.Scalar(c))!;
    }

    public async Task<RecipientPage> Recipients(DateTimeOffset asOf, string? after, string? materialId)
    {
        if (asOf == default || after is not null && !Guid.TryParse(after, out _))
            throw new ApiException(400, "invalid_recipient_cursor", "asOf and a valid company cursor are required.");
        using var connection = await Open();
        var command = new QueryObject(InsightSql.FindQuestionRecipients);
        command.WithParameter("@after", after ?? ""); command.WithParameter("@asOf", asOf.UtcTicks);
        command.WithParameter("@material", (object?)materialId ?? DBNull.Value);
        var ids = new List<Guid>(); using var reader = await connection.Rows(command); while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return new(ids.Take(256).ToArray(), ids.Count > 256 ? ids[255].ToString("D") : null);
    }

    private static async Task<StoredAnalysis?> FindAnalysis(PgSession connection,
        Guid companyId, string materialId, string versionId)
    {

        var command = new QueryObject(InsightSql.FindAnalysisReceipt);
        command.WithParameter("@company", companyId.ToString("D"));
        command.WithParameter("@material", materialId);
        command.WithParameter("@version", versionId);
        using var reader = await connection.Rows(command);
        return reader.Read() ? new StoredAnalysis(reader.GetString(0),
            JsonSerializer.Deserialize<AnalysisReceipt>(reader.GetString(1), AnalysisProtocol.Json)!) : null;
    }

}
