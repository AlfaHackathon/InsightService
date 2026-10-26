using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AlfaHackaton.DapperContext.Common.Interfaces.Dapper;
using AlfaHackaton.DapperContext.Dapper;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.NewsJobs;
using InsightService.Models.Persistence;
using InsightService.Models.Policies;
using InsightService.Repositories.Interfaces;
using InsightService.Repositories.Postgres;
using InsightService.Resources;

namespace InsightService.Repositories;

public sealed class NewsJobRepository(IDapperContext context) : INewsJobRepository
{
    public async Task<Result<NewsJob>> Create(NewsJobRequest request)
    {
        if (RequestValidation.RequireId(request.JobId, "jobId").Error is { } jobIdError)
        {
            return new(jobIdError);
        }
        if (RequestValidation.RequireId(request.NewsMaterialId, "newsMaterialId").Error is { } newsMaterialIdError)
        {
            return new(newsMaterialIdError);
        }
        if (RequestValidation.RequireId(request.NewsVersionId, "newsVersionId").Error is { } newsVersionIdError)
        {
            return new(newsVersionIdError);
        }
        if (request.AsOf == default ||
            request.Parameters.ValueKind != JsonValueKind.Object ||
            request.Parameters.GetRawText().Length > 32000)
        {
            return new(new Error("A job requires asOf and bounded object parameters.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
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
                return new(new Error("A job ID cannot be reused with another request.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
            }

            return new(JsonSerializer.Deserialize<NewsJob>(existing.Payload, AnalysisProtocol.Json)!, null);
        }

        var job = new NewsJob(request, "queued", 0, null, null, JsonSerializer.SerializeToElement(new
        {
        }), null, DateTimeOffset.UtcNow);
        await Write(transaction, job, hash);
        transaction.Commit();
        return new(job, null);
    }

    public async Task<Result<NewsJob>> GetAsync(string id)
    {
        if (RequestValidation.RequireId(id, "jobId").Error is { } jobIdError2)
        {
            return new(jobIdError2);
        }
        using var transaction = await InsightTransaction.OpenAsync(context);
        var readResult = await Read(transaction, id);
        if (readResult is null)
        {
            return new(new Error("News job was not found.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
        }

        return new(readResult, null);
    }

    public Task<SourceCursor> GetSourceCursor(string source = "context") => new SourceCursorRepository(context).GetSourceCursor(source);
    public Task<Result<SourceCursor>> SaveSourceCursor(SourceCursor cursor, string source = "context") => new SourceCursorRepository(context).SaveSourceCursor(cursor, source);
    public async Task<Result<NewsJob?>> Claim(JobClaim claim)
    {
        if (RequestValidation.RequireId(claim.WorkerId, "workerId").Error is { } workerIdError)
        {
            return new(workerIdError);
        }
        using var transaction = await InsightTransaction.OpenAsync(context);
        var id = await transaction.FirstOrDefault<string>(new QueryObject(claim.JobId is null ? InsightSql.FindClaimableJob : InsightSql.FindClaimableJobById,
            new
            {
                now = DateTimeOffset.UtcNow.UtcTicks,
                id = claim.JobId
            }));
        if (id is null)
        {
            return new(null, null);
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
        return new(next, null);
    }

    public async Task<Result<NewsJob>> Update(string id, JobUpdate update)
    {
        if (RequestValidation.RequireId(update.WorkerId, "workerId").Error is { } workerIdError2)
        {
            return new(workerIdError2);
        }
        if (update.Status is not ("running" or "partial" or "completed" or "failed") ||
            !InsightCategoryPolicy.IsValidCheckpoint(update.Checkpoint) ||
            update.Checkpoint.GetRawText().Length > 4000000 ||
            update.Error?.Length > 2000)
        {
            return new(new Error("Status, bounded checkpoint/error and a valid general insight category are required.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.BadRequest });
        }

        using var transaction = await InsightTransaction.OpenAsync(context);
        var readResult2 = await Read(transaction, id);
        if (readResult2 is null)
        {
            return new(new Error("News job was not found.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
        }

        var old = readResult2;
        var now = DateTimeOffset.UtcNow;
        if (old.Status != "running" ||
            old.WorkerId != update.WorkerId ||
            old.Version != update.Version ||
            old.LeaseUntil < now)
        {
            return new(new Error("This worker no longer owns the current job lease/version.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
        }

        if (InsightPlainTextPolicy.ValidateCheckpoint(update.Checkpoint, old.Checkpoint) is { } textError)
        {
            return new(textError);
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
        return new(next, null);
    }

    public async Task<Result<NewsJob>> Retry(string id)
    {
        if (RequestValidation.RequireId(id, "jobId").Error is { } jobIdError3)
        {
            return new(jobIdError3);
        }
        using var transaction = await InsightTransaction.OpenAsync(context);
        var readResult3 = await Read(transaction, id);
        if (readResult3 is null)
        {
            return new(new Error("News job was not found.", ErrorSource.Client, ErrorType.NotFound) { HttpStatusCode = HttpStatusCode.NotFound });
        }

        var old = readResult3;
        if (old.Status != "failed")
        {
            return new(new Error("Only explicitly failed jobs can be retried.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.Conflict });
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
        return new(next, null);
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
        await transaction.Command(new QueryObject(hash is not null ? InsightSql.InsertJob : InsightSql.UpdateJob,
            new
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
