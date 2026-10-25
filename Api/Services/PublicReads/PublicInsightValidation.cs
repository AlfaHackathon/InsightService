using System.Text.Json;
using System.Text.RegularExpressions;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Calculations;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Contracts.Questions;

namespace InsightService.Services.PublicReads;

internal static partial class PublicInsightValidation
{
    internal static readonly string[] Mechanisms = ["financing_cost", "refinancing_access", "lease_cost", "rent_indexation", "demand", "input_cost", "investment", "liquidity", "tax", "supply_capacity", "labor_cost", "deposit_income"];
    private static readonly string[] Frames = ["event_only", "absolute", "budget_delta"];
    private static readonly string[] Bases = ["qualitative", "monthly_run_rate", "annual_run_rate", "contract_total", "actual_cashflow", "budget_total", "unknown"];
    internal static void Require(bool valid, string code = "INSIGHT_READ_CONTRACT")
    {
        if (!valid)
            throw PublicInsightReadException.Contract(code);
    }
    internal static bool Text(string? value, int maximum, int minimum = 1) => value is not null && value.Length >= minimum && value.Length <= maximum;
    internal static bool Identifier(string? value) => Text(value, 200) && !string.IsNullOrWhiteSpace(value);
    internal static bool Hash(string? value) => value is not null && HashPattern().IsMatch(value);
    internal static void Reference(SourceRef? reference) => Require(reference is not null && Identifier(reference.MaterialId) && Identifier(reference.VersionId) && Hash(reference.ContentHash));
    internal static void References(SourceRef[]? refs, int maximum = 64, int minimum = 0)
    {
        Require(refs is not null && refs.Length >= minimum && refs.Length <= maximum);
        foreach (var reference in refs!)
            Reference(reference);
    }
    internal static bool QuestionForIndicator(QuestionIdentity question) => Mechanisms.Contains(question.Mechanism) && Frames.Contains(question.Frame) &&
        Text(question.Unit, 64) && PeriodPattern().IsMatch(question.TargetPeriod) &&
        (question.BaselinePeriod is null || PeriodPattern().IsMatch(question.BaselinePeriod)) &&
        (question.Basis is null || Bases.Contains(question.Basis)) && (question.Frame != "budget_delta" || question.BaselinePeriod is not null);
    internal static void Revision(EffectRevision? revision)
    {
        Require(revision is not null);
        var r = revision!;
        Require(Identifier(r.RevisionId) && (r.PreviousRevisionId is null || Identifier(r.PreviousRevisionId)) && Identifier(r.NewsVersionId) &&
            r.AsOf != default && r.CreatedAt != default && r.Conclusion is not null && Text(r.Status, 100) &&
            (r.NewsMaterialId is null || Identifier(r.NewsMaterialId)) &&
            (r.PublicationClass is null or "consequence" or "scenario" or "missing_data" or "unaffected" or "review"));
        References(r.SourceRefs, int.MaxValue);
        var outerReferences = r.SourceRefs.ToHashSet();
        if (r.Trigger is { } trigger)
            Require(Identifier(trigger.MaterialId) && Identifier(trigger.VersionId));
        if (r.Question is { } q)
        {
            // Historical periods remain readable. Current indicator dimensions are checked separately.
            Require(Mechanisms.Contains(q.Mechanism) && Frames.Contains(q.Frame) && Text(q.Unit, 64) && q.TargetPeriod is not null &&
                (q.Basis is null || Bases.Contains(q.Basis)) && (q.Frame != "budget_delta" || q.BaselinePeriod is not null));
        }
        if (r.Impact is { } impact)
            Require(Identifier(impact.Metric) && Identifier(impact.AmountBasis) &&
            (impact.EconomicObjectId is null || Text(impact.EconomicObjectId, 500)) &&
            impact.EvidenceStatus is "supported" or "conditional" or "unknown" or "contradicted" or "unaffected" &&
            impact.Importance is >= 0 and <= 5 && impact.Urgency is >= 0 and <= 5 && impact.Novelty is >= 0 and <= 5);
        if (r.CompanyImportance is { } importance)
        {
            Require(importance.Score is null or >= 1 and <= 5 && Text(importance.Rationale?.Trim(), 2000));
            References(importance.EvidenceRefs, 16);
            Require(importance.Score is null || importance.EvidenceRefs.Length > 0);
        }
        if (r.Assessment is { } a)
        {
            Require(a.Conditions is { Length: <= 16 } && a.Unknowns is { Length: <= 16 });
            foreach (var condition in a.Conditions)
            {
                Require(condition is not null && Text(condition.Fact, 1000) && Text(condition.Predicate, 1000) && condition.Status is "supported" or "contradicted" or "unknown");
                References(condition!.SourceRefs);
                Require(condition.SourceRefs.All(outerReferences.Contains));
                Require((condition.Id is null || EvidenceIdPattern().IsMatch(condition.Id)) &&
                    (condition.Assertion is null or "observed" or "derived" or "conditional") &&
                    (condition.DependsOn is null || condition.DependsOn.Length <= 16 && condition.DependsOn.All(id => id is not null && EvidenceIdPattern().IsMatch(id))));
            }
            foreach (var missing in a.Unknowns)
            {
                Require(missing is not null && Text(missing.Fact, 1000) && (missing.Query is null || Text(missing.Query, 2000)) &&
                    missing.Scope is "company" or "market" && (missing.MaterialId is null || Identifier(missing.MaterialId)) &&
                    (missing.Id is null || EvidenceIdPattern().IsMatch(missing.Id)));
            }
            if (a.Applicability is { } app)
            {
                Require(app.Status is "established" or "unknown" or "excluded" && app.Basis is "documented_exposure" or "profile_signal" or "market_only" or "not_established" && Text(app.Explanation, 2000));
                References(app.SourceRefs, 16);
                Require(app.SourceRefs.All(outerReferences.Contains));
                Strings(app.Assumptions, 8, 1000);
            }
        }
        if (r.Calculation is { } calculation)
        {
            // Legacy unknown/empty units remain internal and suppress indicators, rather than hiding cards.
            Require(calculation.Unit is not null && Text(calculation.Period, 1000));
            var count = 0;
            Expression(calculation.Expression, 1, ref count);
        }
    }
    private static void Expression(ExpressionNode? node, int depth, ref int count)
    {
        Require(node is not null && ++count <= 127 && depth <= 12);
        if (node!.Kind == "value")
            Require(Text(node.Value, 100) && DecimalLiteralPattern().IsMatch(node.Value!) && node.Left is null && node.Right is null);
        else
        {
            Require(node.Kind is "add" or "subtract" or "multiply" or "divide" or "min" or "max" && node.Value is null);
            Expression(node.Left, depth + 1, ref count);
            Expression(node.Right, depth + 1, ref count);
        }
    }
    internal static void Source(MaterialVersion source)
    {
        Require(Identifier(source.MaterialId) && Identifier(source.VersionId) && Identifier(source.SourceId) && Hash(source.ContentHash) &&
            source.Kind is "news" or "company" or "market" && source.Title is not null && source.Text is not null && source.KnownAt != default &&
            source.CompanyIds is not null && source.BankProfileIds is not null && source.BankProfileIds.All(id => id is not null && BankPattern().IsMatch(id)));
        if (source.Provenance is { } provenance)
        {
            Require(provenance.Acquisition is "uploaded" or "event_payload" or "news_service" && provenance.RetrievedAt != default &&
                provenance.TextCompleteness is "full" or "lead" or "excerpt" or "unknown" &&
                (provenance.SourceUrl is null || Uri.TryCreate(provenance.SourceUrl, UriKind.Absolute, out _)) &&
                (provenance.OriginVersionId is null || Identifier(provenance.OriginVersionId)));
        }
    }
    private static void Strings(string[]? values, int maximum, int length, int minimum = 1) =>
        Require(values is not null && values.Length <= maximum && values.All(value => Text(value, length, minimum)));
    internal static PublicGeneralInsight General(JsonElement json)
    {
        Require(json.ValueKind == JsonValueKind.Object);
        PublicGeneralInsight value;
        try
        { value = json.Deserialize<PublicGeneralInsight>(AnalysisProtocol.Json) ?? throw PublicInsightReadException.Contract(); }
        catch (JsonException) { throw PublicInsightReadException.Contract(); }
        Require(Text(value.Title, 500) && Text(value.Conclusion, 6000) && Text(value.Mechanism, 4000));
        Strings(value.Applicability, 16, 1000, 0);
        Strings(value.Exclusions, 16, 1000, 0);
        Strings(value.Unknowns, 16, 1000, 0);
        Require(value.SourceRefs is { Length: >= 1 and <= 32 });
        foreach (var r in value.SourceRefs)
            Require(r is not null && Identifier(r.MaterialId) && Identifier(r.VersionId) && Hash(r.ContentHash));
        if (value.SignalIds is { } signals)
            Require(signals.Length <= 128 && signals.All(s => s is not null));
        if (value.Quality is { } quality)
            Require(Text(quality.DecisionQuestion, 700) && Text(quality.DistinctContribution, 700) && quality.Priority is "high" or "medium" or "low" && quality.Novelty is "news_specific" or "source_context");
        // Base release has no frozen category contract. Shared category integration replaces this one assignment.
        return value with { Category = null };
    }
    internal static bool TryInstant(string? text, out DateTimeOffset instant)
    {
        instant = default;
        return text is not null && TimestampPattern().IsMatch(text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out instant);
    }
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)] private static partial Regex HashPattern();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)] private static partial Regex EvidenceIdPattern();
    [GeneratedRegex("^U_[0-9]+$", RegexOptions.CultureInvariant)] private static partial Regex BankPattern();
    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)] private static partial Regex DecimalLiteralPattern();
    [GeneratedRegex(@"^(?:unknown|(?:(?:from|budget|actual|previous):)?[0-9]{4}(?:-(?:Q[1-4]|(?:0[1-9]|1[0-2])(?:-(?:0[1-9]|[12][0-9]|3[01]))?))?)$", RegexOptions.CultureInvariant)] internal static partial Regex PeriodPattern();
    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)] private static partial Regex TimestampPattern();
}
