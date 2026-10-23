using System.Net;
using System.Text.Json;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Common;
using InsightService.Contracts.Execution;
using InsightService.Models;
using InsightService.Repositories.Interfaces;

namespace InsightService.Services;

public sealed class ExecutionJobReader(INewsJobRepository jobs)
{
    public async Task<Result<ExecutionJobContext>> ReadAsync(string jobId)
    {
        if (RequestValidation.RequireId(jobId, "jobId").Error is { } idError)
        {
            return new(idError);
        }
        var found = await jobs.GetAsync(jobId);
        if (found.Error is { } error)
        {
            return new(error);
        }
        var job = found.Content!;
        try
        {
            var request = job.Request.Parameters.Deserialize<JobFlowParameters>(AnalysisProtocol.Json);
            var checkpoint = job.Checkpoint.Deserialize<JobExecutionCheckpoint>(AnalysisProtocol.Json);
            if (request is null || checkpoint is null || request.NewsMaterialId != job.Request.NewsMaterialId ||
                request.NewsVersionId != job.Request.NewsVersionId || request.AsOf != job.Request.AsOf ||
                request.GroupIds is null || request.PersonIds is null || request.TargetCompanyIds is null ||
                request.GroupIds.Length > 64 || request.PersonIds.Length > 64 || request.CompanyBudget is < 1 or > 64 ||
                request.TextCompleteness is not ("full" or "lead" or "unknown") || checkpoint.Companies is null ||
                checkpoint.SelectedCompanyIds is null || checkpoint.PendingCompanyIds is null || checkpoint.Groups is null ||
                checkpoint.SegmentBatches is null || checkpoint.SignalInventories is null || checkpoint.RoleViews is null ||
                checkpoint.Companies.Length > 10000 || checkpoint.Groups.Length > 64 || checkpoint.SelectedCompanyIds.Length > 10000 ||
                checkpoint.Plan.ValueKind != JsonValueKind.Object)
            {
                return new(InvalidCheckpoint());
            }
            return new(new ExecutionJobContext { Job = job, Request = request, Checkpoint = checkpoint }, null);
        }
        catch (JsonException)
        {
            return new(InvalidCheckpoint());
        }
    }

    private static Error InvalidCheckpoint() => new("The persisted job scope or planning checkpoint is invalid.", ErrorSource.Client, ErrorType.CorruptedInfo) { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
