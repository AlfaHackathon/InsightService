using System.Net;
using AlfaHackaton.Result;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;
using InsightService.Services.Interfaces;

namespace InsightService.Services;

public class AnalysisSessionService(IAnalysisService analyses)
{
    public async Task<Result<AnalysisSessionResponse>> OpenAsync(AnalysisRequest request)
    {
        var normalized = AnalysisRequestNormalizer.Normalize(request);
        if (normalized.Error is { } validationError)
        {
            return new(validationError);
        }

        var input = normalized.Content.Item1;
        var completed = await analyses.GetAnalysis(input.CompanyId, input.NewsMaterialId, input.NewsVersionId, input.Trigger);
        if (completed.Error is { } readError && readError.HttpStatusCode != HttpStatusCode.NotFound)
        {
            return new(readError);
        }

        if (completed.Content is { } receipt)
        {
            if (receipt.AsOf != input.AsOf || receipt.BankProfileId != input.BankProfileId)
            {
                return new(new Error("Эта версия новости уже обработана с другими параметрами анализа", ErrorSource.Client, ErrorType.CorruptedInfo)
                {
                    HttpStatusCode = HttpStatusCode.Conflict
                });
            }

            return new(new AnalysisSessionResponse { Receipt = receipt with { Duplicate = true } }, null);
        }

        var snapshot = await analyses.GetSnapshot(input.CompanyId, input.NewsMaterialId, input.AsOf);
        if (snapshot.Error is { } snapshotError)
        {
            return new(snapshotError);
        }

        return new(new AnalysisSessionResponse
        {
            PreviousEffects = snapshot.Content!.Effects
                .Where(effect => input.Mechanisms is null || effect.CurrentRevision.Question is null
                    || input.Mechanisms.Contains(effect.CurrentRevision.Question.Mechanism, StringComparer.Ordinal))
                .ToArray(),
            StateVersion = snapshot.Content.StateVersion
        }, null);
    }
}
