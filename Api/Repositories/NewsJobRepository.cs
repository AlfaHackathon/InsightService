using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Migrations;
using InsightService.Models;
using InsightService.Repositories.Interfaces;
using InsightService.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InsightService.Repositories.Postgres;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;

namespace InsightService.Repositories;

public sealed class NewsJobRepository : INewsJobRepository
{
    private readonly IDapperContext context;
    public NewsJobRepository(IDapperContext context) => this.context = context;

    public async Task<NewsJob> Create(NewsJobRequest request)
    {
        RequestValidation.RequireId(request.JobId, "jobId"); RequestValidation.RequireId(request.NewsMaterialId, "newsMaterialId");
        RequestValidation.RequireId(request.NewsVersionId, "newsVersionId");
        if (request.AsOf == default || request.Parameters.ValueKind != JsonValueKind.Object || request.Parameters.GetRawText().Length > 32000)
            throw new ApiException(400, "invalid_job", "A job requires asOf and bounded object parameters.");
        request = request with { AsOf = request.AsOf.ToUniversalTime() };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AnalysisProtocol.Json))));
        using var c = await Open();
        {

            var check = new QueryObject(InsightSql.FindJobRequest);
            check.WithParameter("@id", request.JobId); using var reader = await c.Rows(check);
            if (reader.Read())
            {
                if (reader.GetString(0) != hash) throw new ApiException(409, "job_request_conflict", "A job ID cannot be reused with another request.");
                return JsonSerializer.Deserialize<NewsJob>(reader.GetString(1), AnalysisProtocol.Json)!;
            }
        }
        var empty = JsonSerializer.SerializeToElement(new { });
        var job = new NewsJob(request, "queued", 0, null, null, empty, null, DateTimeOffset.UtcNow);
        await Write(c, job, hash); c.Commit(); return job;
    }
    public async Task<NewsJob> Get(string id)
    {
        RequestValidation.RequireId(id, "jobId"); using var c = await Open();
        return await Read(c, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
    }
    public async Task<SourceCursor> GetSourceCursor() => await new SourceCursorRepository(Open).GetSourceCursor();
    public async Task<SourceCursor> SaveSourceCursor(SourceCursor cursor) => await new SourceCursorRepository(Open).SaveSourceCursor(cursor);
    public async Task<NewsJob?> Claim(JobClaim claim)
    {
        RequestValidation.RequireId(claim.WorkerId, "workerId");
        using var c = await Open();
        var query = new QueryObject(claim.JobId is null ? InsightSql.FindClaimableJob : InsightSql.FindClaimableJobById);
        query.WithParameter("@now", DateTimeOffset.UtcNow.UtcTicks);
        if (claim.JobId is not null) query.WithParameter("@id", claim.JobId);
        var id = (await c.Scalar(query)) as string; if (id is null) return null;
        var old = (await Read(c, id))!; var now = DateTimeOffset.UtcNow;
        var next = old with
        {
            Status = "running",
            Version = old.Version + 1,
            WorkerId = claim.WorkerId,
            LeaseUntil = now.AddMinutes(2),
            UpdatedAt = now,
            Error = null
        };
        await Write(c, next); c.Commit(); return next;
    }
    public async Task<NewsJob> Update(string id, JobUpdate update)
    {
        RequestValidation.RequireId(update.WorkerId, "workerId");
        if (update.Status is not ("running" or "partial" or "completed" or "failed") || update.Checkpoint.ValueKind != JsonValueKind.Object
            || update.Checkpoint.GetRawText().Length > 4000000 || update.Error?.Length > 2000)
            throw new ApiException(400, "invalid_job_update", "Status and bounded checkpoint/error are required.");
        using var c = await Open();
        var old = await Read(c, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
        var now = DateTimeOffset.UtcNow;
        if (old.Status != "running" || old.WorkerId != update.WorkerId || old.Version != update.Version || old.LeaseUntil < now)
            throw new ApiException(409, "job_lease_conflict", "This worker no longer owns the current job lease/version.");
        var next = old with
        {
            Status = update.Status,
            Version = old.Version + 1,
            UpdatedAt = now,
            LeaseUntil = update.Status == "running" ? now.AddMinutes(2) : null,
            Checkpoint = update.Checkpoint.Clone(),
            Error = update.Error
        };
        await Write(c, next); c.Commit(); return next;
    }
    public async Task<NewsJob> Retry(string id)
    {
        RequestValidation.RequireId(id, "jobId"); using var c = await Open();
        var old = await Read(c, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
        if (old.Status != "failed") throw new ApiException(409, "job_not_failed", "Only explicitly failed jobs can be retried.");
        var next = old with { Status = "queued", Version = old.Version + 1, WorkerId = null, LeaseUntil = null, UpdatedAt = DateTimeOffset.UtcNow };
        await Write(c, next); c.Commit(); return next;
    }
    private static async Task<NewsJob?> Read(PgSession c, string id)
    {
        var cmd = new QueryObject(InsightSql.ReadJob); cmd.WithParameter("@id", id);
        return (await c.Scalar(cmd)) is string json ? JsonSerializer.Deserialize<NewsJob>(json, AnalysisProtocol.Json) : null;
    }
    private static async Task Write(PgSession c, NewsJob job, string? hash = null)
    {
        var cmd = new QueryObject(hash is not null ? InsightSql.InsertJob : InsightSql.UpdateJob);
        cmd.WithParameter("@id", job.Request.JobId); if (hash is not null) cmd.WithParameter("@hash", hash);
        cmd.WithParameter("@s", job.Status); cmd.WithParameter("@v", job.Version);
        cmd.WithParameter("@w", (object?)job.WorkerId ?? DBNull.Value);
        cmd.WithParameter("@l", (object?)job.LeaseUntil?.UtcTicks ?? DBNull.Value);
        cmd.WithParameter("@t", job.UpdatedAt.UtcTicks);
        cmd.WithParameter("@p", JsonSerializer.Serialize(job, AnalysisProtocol.Json)); await c.Execute(cmd);
        var audit = new QueryObject(InsightSql.InsertJobEvent);
        audit.WithParameter("@id", job.Request.JobId); audit.WithParameter("@v", job.Version);
        audit.WithParameter("@p", JsonSerializer.Serialize(job, AnalysisProtocol.Json)); await c.Execute(audit);
    }
    private Task<PgSession> Open() => PgSession.Open(context);
}
