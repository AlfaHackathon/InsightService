using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Models;
using InsightService.Models.Persistence;
using InsightService.Repositories.Interfaces;
using InsightService.Repositories.Postgres;
using InsightService.Resources;

namespace InsightService.Repositories;

public sealed class NewsJobRepository(IDapperContext context) : INewsJobRepository
{
    public async Task<NewsJob> Create(NewsJobRequest request)
    {
        RequestValidation.RequireId(request.JobId, "jobId");
        RequestValidation.RequireId(request.NewsMaterialId, "newsMaterialId");
        RequestValidation.RequireId(request.NewsVersionId, "newsVersionId");
        if (request.AsOf == default || request.Parameters.ValueKind != JsonValueKind.Object || request.Parameters.GetRawText().Length > 32000)
        {
            throw new ApiException(400, "invalid_job", "A job requires asOf and bounded object parameters.");
        }
        request = request with
        {
            AsOf = request.AsOf.ToUniversalTime()
        };
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, AnalysisProtocol.Json))));
        using var transaction = await InsightTransaction.OpenAsync(context);
        var existing = await transaction.FirstOrDefault<NewsJobRow>(new QueryObject(InsightSql.FindJobRequest, new
        {
            id = request.JobId
        }));
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
            {
                throw new ApiException(409, "job_request_conflict", "A job ID cannot be reused with another request.");
            }
            return JsonSerializer.Deserialize<NewsJob>(existing.Payload, AnalysisProtocol.Json)!;
        }
        var job = new NewsJob(request, "queued", 0, null, null, JsonSerializer.SerializeToElement(new
        {
        }), null, DateTimeOffset.UtcNow);
        await Write(transaction, job, hash);
        transaction.Commit();
        return job;
    }

    public async Task<NewsJob> GetAsync(string id)
    {
        RequestValidation.RequireId(id, "jobId");
        using var transaction = await InsightTransaction.OpenAsync(context);
        return await Read(transaction, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
    }

    public Task<SourceCursor> GetSourceCursor() => new SourceCursorRepository(context).GetSourceCursor();

    public Task<SourceCursor> SaveSourceCursor(SourceCursor cursor) => new SourceCursorRepository(context).SaveSourceCursor(cursor);

    public async Task<NewsJob?> Claim(JobClaim claim)
    {
        RequestValidation.RequireId(claim.WorkerId, "workerId");
        using var transaction = await InsightTransaction.OpenAsync(context);
        var id = await transaction.FirstOrDefault<string>(new QueryObject(
            claim.JobId is null ? InsightSql.FindClaimableJob : InsightSql.FindClaimableJobById,
            new
            {
                now = DateTimeOffset.UtcNow.UtcTicks,
                id = claim.JobId
            }));
        if (id is null)
        {
            return null;
        }
        var old = (await Read(transaction, id))!;
        var now = DateTimeOffset.UtcNow;
        var next = old with
        {
            Status = "running",
            Version = old.Version + 1,
            WorkerId = claim.WorkerId,
            LeaseUntil = now.AddMinutes(2),
            UpdatedAt = now,
            Error = null
        };
        await Write(transaction, next);
        transaction.Commit();
        return next;
    }

    public async Task<NewsJob> Update(string id, JobUpdate update)
    {
        RequestValidation.RequireId(update.WorkerId, "workerId");
        if (update.Status is not ("running" or "partial" or "completed" or "failed") || update.Checkpoint.ValueKind != JsonValueKind.Object
            || update.Checkpoint.GetRawText().Length > 4000000 || update.Error?.Length > 2000)
        {
            throw new ApiException(400, "invalid_job_update", "Status and bounded checkpoint/error are required.");
        }
        using var transaction = await InsightTransaction.OpenAsync(context);
        var old = await Read(transaction, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
        var now = DateTimeOffset.UtcNow;
        if (old.Status != "running" || old.WorkerId != update.WorkerId || old.Version != update.Version || old.LeaseUntil < now)
        {
            throw new ApiException(409, "job_lease_conflict", "This worker no longer owns the current job lease/version.");
        }
        var next = old with
        {
            Status = update.Status,
            Version = old.Version + 1,
            UpdatedAt = now,
            LeaseUntil = update.Status == "running" ? now.AddMinutes(2) : null,
            Checkpoint = update.Checkpoint.Clone(),
            Error = update.Error
        };
        await Write(transaction, next);
        transaction.Commit();
        return next;
    }

    public async Task<NewsJob> Retry(string id)
    {
        RequestValidation.RequireId(id, "jobId");
        using var transaction = await InsightTransaction.OpenAsync(context);
        var old = await Read(transaction, id) ?? throw new ApiException(404, "job_not_found", "News job was not found.");
        if (old.Status != "failed")
        {
            throw new ApiException(409, "job_not_failed", "Only explicitly failed jobs can be retried.");
        }
        var next = old with
        {
            Status = "queued",
            Version = old.Version + 1,
            WorkerId = null,
            LeaseUntil = null,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await Write(transaction, next);
        transaction.Commit();
        return next;
    }

    private static async Task<NewsJob?> Read(ITransaction transaction, string id)
    {
        var json = await transaction.FirstOrDefault<string>(new QueryObject(InsightSql.ReadJob, new
        {
            id
        }));
        return json is null ? null : JsonSerializer.Deserialize<NewsJob>(json, AnalysisProtocol.Json);
    }

    private static async Task Write(ITransaction transaction, NewsJob job, string? hash = null)
    {
        var payload = JsonSerializer.Serialize(job, AnalysisProtocol.Json);
        await transaction.Command(new QueryObject(hash is not null ? InsightSql.InsertJob : InsightSql.UpdateJob, new
        {
            id = job.Request.JobId,
            hash,
            s = job.Status,
            v = job.Version,
            w = job.WorkerId,
            l = job.LeaseUntil?.UtcTicks,
            t = job.UpdatedAt.UtcTicks,
            p = payload
        }));
        await transaction.Command(new QueryObject(InsightSql.InsertJobEvent, new
        {
            id = job.Request.JobId,
            v = job.Version,
            p = payload
        }));
    }
}
