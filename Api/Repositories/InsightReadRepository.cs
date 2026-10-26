using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Reads;
using InsightService.Models.Policies;
using InsightService.Resources;

namespace InsightService.Repositories;

public sealed class InsightReadRepository(IDapperContext context)
{
    private sealed record PersonalRow(string Id, Guid CompanyId, string NewsMaterialId, string LiteralObjectRef,
        string RevisionJson, long OrderTicks, bool RequiresReassessment);
    private sealed record HistoryRow(long Sequence, string RevisionJson);
    private sealed record GeneralRow(string JobId, int InsightIndex, long OrderTicks, string NewsMaterialId,
        string NewsVersionId, string AsOf, string UpdatedAt, string TextCompleteness, string CompanyStatus, string InsightJson);
    private sealed record GeneralKey(string JobId, int Index);

    private static EffectRevision Revision(string json) => JsonSerializer.Deserialize<EffectRevision>(json, AnalysisProtocol.Json)
        ?? throw new InvalidOperationException("Invalid persisted revision.");
    private static PersonalInsightRead Personal(PersonalRow row) => new(row.Id, row.CompanyId,
        row.NewsMaterialId, row.LiteralObjectRef, Revision(row.RevisionJson), row.RequiresReassessment);
    private static GeneralInsightRead General(GeneralRow row, Guid company) => new(
        InsightReadCursor.EncodeValue(new GeneralKey(row.JobId, row.InsightIndex)), company, row.JobId, row.InsightIndex,
        row.NewsMaterialId, row.NewsVersionId, row.AsOf, row.UpdatedAt, row.TextCompleteness, row.CompanyStatus,
        JsonSerializer.Deserialize<JsonElement>(row.InsightJson, AnalysisProtocol.Json));

    private static void Scope(Guid company, int limit)
    {
        if (company == Guid.Empty || limit is < 1 or > 20)
        {
            throw new BadHttpRequestException("Invalid read scope or limit.");
        }
    }
    private static void Id(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200)
        {
            throw new BadHttpRequestException("Invalid insight identifier.");
        }
    }
    private static string Filter(InsightReadOptions options)
    {
        if (!InsightCategoryPolicy.IsValid(options.Category) || options.Status?.Length > 100 || options.Mechanism?.Length > 100 || options.NewsMaterialId?.Length > 200 || options.MinImportance is < 1 or > 5)
        {
            throw new BadHttpRequestException("Invalid read filters.");
        }
        var values = new List<object?> { options.Status, options.Mechanism, options.NewsMaterialId, options.MinImportance };
        if (options.Category is not null)
        {
            values.Add(options.Category);
        }
        return JsonSerializer.Serialize(values, AnalysisProtocol.Json);
    }

    public async Task<InsightReadPage<PersonalInsightRead>> PersonalPage(Guid company, InsightReadOptions options)
    {
        Scope(company, options.Limit);
        var filter = Filter(options);
        var cursor = InsightReadCursor.Read(options.Cursor, company, "personal", filter);
        using var transaction = context.BeginTransaction();
        await transaction.Command(new QueryObject(InsightReadSql.ReadTransaction));
        var rows = await transaction.ListOrEmpty<PersonalRow>(new QueryObject(InsightReadSql.PersonalPage, new
        {
            company = company.ToString("D"),
            now = DateTimeOffset.UtcNow.UtcTicks,
            take = options.Limit + 1,
            status = options.Status,
            mechanism = options.Mechanism,
            category = options.Category,
            news = options.NewsMaterialId,
            importance = options.MinImportance,
            after = cursor?.After,
            id = cursor?.Id
        }));
        var selected = rows.Take(options.Limit).ToArray();
        var last = selected.LastOrDefault();
        transaction.Commit();
        return new(company, selected.Select(Personal).ToArray(), rows.Count > options.Limit && last is not null
            ? new InsightReadCursor(1, company, "personal", filter, last.OrderTicks, last.Id).Encode() : null);
    }

    public async Task<PersonalInsightRead?> PersonalDetail(Guid company, string id)
    {
        Scope(company, 1);
        Id(id);
        using var transaction = context.BeginTransaction();
        await transaction.Command(new QueryObject(InsightReadSql.ReadTransaction));
        var row = await transaction.FirstOrDefault<PersonalRow>(new QueryObject(InsightReadSql.PersonalDetail,
            new { company = company.ToString("D"), id, now = DateTimeOffset.UtcNow.UtcTicks }));
        transaction.Commit();
        return row is null ? null : Personal(row);
    }

    public async Task<InsightReadPage<EffectRevision>?> PersonalHistory(Guid company, string id, int limit, string? after)
    {
        Scope(company, limit);
        Id(id);
        var cursor = InsightReadCursor.Read(after, company, "history", id);
        if (await PersonalDetail(company, id) is null)
        {
            return null;
        }
        using var transaction = context.BeginTransaction();
        await transaction.Command(new QueryObject(InsightReadSql.ReadTransaction));
        var rows = await transaction.ListOrEmpty<HistoryRow>(new QueryObject(InsightReadSql.PersonalHistory,
            new { company = company.ToString("D"), id, after = cursor?.After, take = limit + 1 }));
        var selected = rows.Take(limit).ToArray();
        var last = selected.LastOrDefault();
        transaction.Commit();
        return new(company, selected.Select(row => Revision(row.RevisionJson)).ToArray(), rows.Count > limit && last is not null
            ? new InsightReadCursor(1, company, "history", id, last.Sequence, id).Encode() : null);
    }

    public async Task<InsightReadPage<GeneralInsightRead>> GeneralPage(Guid company, InsightReadOptions options)
    {
        Scope(company, options.Limit);
        if (options.Status is not null || options.Mechanism is not null || options.MinImportance is not null)
        {
            throw new BadHttpRequestException("These filters apply to personal insights only.");
        }
        var filter = Filter(options);
        var cursor = InsightReadCursor.Read(options.Cursor, company, "general", filter);
        using var transaction = context.BeginTransaction();
        await transaction.Command(new QueryObject(InsightReadSql.ReadTransaction));
        var rows = await transaction.ListOrEmpty<GeneralRow>(new QueryObject(InsightReadSql.GeneralPage, new
        {
            company = company.ToString("D"),
            category = options.Category,
            news = options.NewsMaterialId,
            take = options.Limit + 1,
            after = cursor?.After,
            id = cursor?.Id,
            index = cursor?.Index
        }));
        var selected = rows.Take(options.Limit).ToArray();
        var last = selected.LastOrDefault();
        transaction.Commit();
        return new(company, selected.Select(row => General(row, company)).ToArray(), rows.Count > options.Limit && last is not null
            ? new InsightReadCursor(1, company, "general", filter, last.OrderTicks, last.JobId, last.InsightIndex).Encode() : null);
    }

    public async Task<GeneralInsightRead?> GeneralDetail(Guid company, string id)
    {
        Scope(company, 1);
        var key = InsightReadCursor.DecodeValue<GeneralKey>(id);
        Id(key.JobId);
        if (key.Index is < 0 or > 23)
        {
            throw new BadHttpRequestException("Invalid general insight index.");
        }
        using var transaction = context.BeginTransaction();
        await transaction.Command(new QueryObject(InsightReadSql.ReadTransaction));
        var row = await transaction.FirstOrDefault<GeneralRow>(new QueryObject(InsightReadSql.GeneralDetail,
            new { company = company.ToString("D"), id = key.JobId, index = key.Index }));
        transaction.Commit();
        return row is null ? null : General(row, company);
    }
}
