using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AlfaHackaton.Result;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.PublicReads;
using InsightService.Models.Policies;

namespace InsightService.Services.PublicReads;

internal static partial class PublicInsightProjection
{
    internal static PublicInsightSourceRef Reference(SourceRef reference) => new(reference.MaterialId, reference.VersionId, reference.ContentHash);
    private static PublicInsightSourceRef[] References(SourceRef[] refs) => refs.Select(Reference).ToArray();
    internal static PublicInsightRevision Revision(EffectRevision r)
    {
        var a = r.Assessment;
        var app = a?.Applicability;
        var q = r.Question;
        return new(r.RevisionId, r.PreviousRevisionId, r.NewsVersionId, r.AsOf, r.Conclusion, r.Status, References(r.SourceRefs), r.CreatedAt,
            r.Impact is { } impact ? new(impact.Metric, impact.AmountBasis, impact.EconomicObjectId, impact.EvidenceStatus) : null,
            r.CompanyImportance is { } rating ? new(rating.Score, rating.Rationale.Trim(), References(rating.EvidenceRefs)) : null,
            r.NewsMaterialId, q is null ? null : new(q.Mechanism, q.Frame, q.Unit, q.TargetPeriod, q.BaselinePeriod, q.Basis),
            a is null ? null : new(a.Conditions.Select(c => new PublicInsightCondition(c.Fact, c.Predicate, c.Status, References(c.SourceRefs), c.Id, c.Assertion, c.DependsOn)).ToArray(),
                a.Unknowns.Select(u => new PublicInsightMissingFact(u.Fact, u.Query, u.Scope, u.MaterialId, u.Id)).ToArray(),
                app is null ? null : new(app.Status, app.Basis, app.Explanation, References(app.SourceRefs), app.Assumptions)),
            r.Trigger is { } trigger ? new(trigger.MaterialId, trigger.VersionId) : null, r.PublicationClass,
            Category: null); // Exactly one stored-property assignment after the shared category model is integrated.
    }
    internal static PublicInsightSource Metadata(MaterialVersion source)
    {
        var p = source.Provenance;
        return new(source.MaterialId, source.VersionId, source.ContentHash, source.SourceId, source.Kind, source.Title, source.KnownAt, source.PublishedAt,
            p is null ? null : new(p.Acquisition, SafeUrl(p.SourceUrl), p.RetrievedAt, p.TextCompleteness, p.OriginVersionId));
    }
    private static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? value : null;
    internal static (PublicInsightIndicator[] Items, PublicIndicatorAvailability Availability) Unavailable(string reason) => ([], new(reason, reason switch
    {
        "no_calculation" => "В этой ревизии нет обоснованного расчёта.",
        "missing_result" => "Расчётный результат неизвестен; нулём не подменяется.",
        "stale_sources" => "Основания требуют пересмотра; актуальный показатель не подтверждён.",
        "insufficient_evidence" => "Недостаточно подтверждённых собственных оснований для показателя.",
        "unknown_period" => "Период расчёта не установлен.",
        "unknown_baseline" => "База бюджетного сравнения не установлена.",
        "general_not_company_calculation" => "Общий вывод не является расчётом показателей компании.",
        _ => throw new InvalidOperationException("Unknown indicator availability reason.")
    }));

    internal static EffectDraft Draft(EffectRevision r) => new("public-read", "public-read", null, r.Conclusion, r.Status, r.SourceRefs,
        r.Calculation, r.Impact, r.Question, r.Assessment, CompanyImportance: r.CompanyImportance);

    internal static async Task<(PublicInsightIndicator[] Items, PublicIndicatorAvailability Availability)> Indicators(EffectRevision r,
        bool stale, IReadOnlyList<MaterialVersion> sources, Guid company)
    {
        if (stale)
            return Unavailable("stale_sources");
        if (r.Calculation is not { } calculation)
            return Unavailable("no_calculation");
        if (r.CalculationResult is null || !DecimalResultPattern().IsMatch(r.CalculationResult))
            return Unavailable("missing_result");
        if (r.Question is not { } question || r.Assessment is not { } assessment)
            return Unavailable("insufficient_evidence");
        if (!PublicInsightValidation.QuestionForIndicator(question) || question.TargetPeriod == "unknown")
            return Unavailable("unknown_period");
        var unit = TrimUnit(question.Unit);
        if (question.Unit != calculation.Unit || question.TargetPeriod != calculation.Period ||
            unit.Length == 0 || unit.ToLowerInvariant() is "unknown" or "qualitative")
            return Unavailable("insufficient_evidence");
        var applicability = assessment.Applicability;
        var cited = r.SourceRefs.ToHashSet();
        var documentary = sources.Where(source => source.Kind == "company" && source.SourceId != "provided-bank-csv" &&
            source.CompanyIds.Contains(company) && cited.Contains(new(source.MaterialId, source.VersionId, source.ContentHash)) &&
            applicability?.SourceRefs.Contains(new(source.MaterialId, source.VersionId, source.ContentHash)) == true).ToArray();
        var status = r.Impact?.EvidenceStatus;
        if (documentary.Length == 0 || applicability is not { Status: "established", Basis: "documented_exposure" } ||
            status is not ("supported" or "conditional") || applicability.SourceRefs.Any(reference => !cited.Contains(reference)))
            return Unavailable("insufficient_evidence");
        var objectId = r.Impact?.EconomicObjectId;
        if (string.IsNullOrWhiteSpace(objectId) || objectId.Any(char.IsControl) || !documentary.Any(source => ContainsIdentifier(source.Text, objectId)))
            return Unavailable("insufficient_evidence");
        if ((status == "conditional" && applicability.Assumptions.Length == 0) ||
            (status == "supported" && (applicability.Assumptions.Length > 0 || assessment.Unknowns.Length > 0 || assessment.Conditions.Any(c => c.Status != "supported"))) ||
            (r.PublicationClass is not null && r.PublicationClass != (status == "supported" ? "consequence" : "scenario")) ||
            assessment.Conditions.Any(c => c.SourceRefs.Any(reference => !cited.Contains(reference)) || c.Status != "unknown" && c.SourceRefs.Length == 0))
            return Unavailable("insufficient_evidence");
        var byIdentity = sources.GroupBy(source => (source.MaterialId, source.VersionId)).ToDictionary(group => group.Key, group => group.First());
        Task<Result<MaterialVersion>> Resolve(string material, string version) => Task.FromResult(new Result<MaterialVersion>(byIdentity[(material, version)], null));
        // Reuse the canonical DAG/applicability policy on a read-only draft. A failed guard suppresses only the indicator.
        if ((await ApplicabilityPolicy.Validate(Draft(r), company, Resolve, validateFinancialSemantics: false)).Error is not null)
            return Unavailable("insufficient_evidence");
        var delta = r.Impact?.Metric is "expense_delta" or "revenue_delta" or "cashflow_delta";
        if (question.Frame == "absolute" && delta || question.Frame != "budget_delta" && question.BaselinePeriod?.StartsWith("budget:", StringComparison.Ordinal) == true)
            return Unavailable("insufficient_evidence");
        if (calculation.Period == "unknown")
            return Unavailable("unknown_period");
        if (question.Frame == "budget_delta")
        {
            var baseline = question.BaselinePeriod;
            var proven = baseline?.StartsWith("budget:", StringComparison.Ordinal) == true && assessment.Conditions.Any(condition =>
                condition is { Status: "supported", Assertion: "observed", Predicate: "budget_baseline", DependsOn.Length: 0 } &&
                ContainsIdentifier(condition.Fact, baseline) && ContainsIdentifier(condition.Fact, objectId) && documentary.Any(source =>
                    source.Text.Contains(condition.Fact, StringComparison.Ordinal) && condition.SourceRefs.Contains(new(source.MaterialId, source.VersionId, source.ContentHash))));
            if (!proven)
                return Unavailable("unknown_baseline");
        }
        // Do not parse/recalculate/rescale this string. Null is never a monetary zero.
        var metric = r.Impact!.Metric;
        var name = metric switch { "expense_delta" => "Изменение расходов", "revenue_delta" => "Изменение выручки", "cashflow_delta" => "Изменение денежного потока", "absolute_expense" => "Расходы", _ => "Расчётный показатель" };
        return ([new(name, metric, r.CalculationResult, calculation.Unit, calculation.Period, question.BaselinePeriod is null or "unknown" ? null : question.BaselinePeriod,
            status, References(r.SourceRefs), r.CalculationArithmeticOnly)], new("available", "Показатель из сохранённого расчёта; статус основания указан отдельно."));
    }

    // Match String.trim's WhiteSpace and LineTerminator sets, including U+FEFF.
    private static string TrimUnit(string value)
    {
        static bool Space(char character) => character is '\u0009' or '\u000B' or '\u000C' or '\uFEFF' or '\u000A' or '\u000D' or '\u2028' or '\u2029' ||
            char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator;
        var start = 0;
        var end = value.Length;
        while (start < end && Space(value[start]))
            start++;
        while (end > start && Space(value[end - 1]))
            end--;
        return value[start..end];
    }

    // JS parity: Unicode Letter OR Number (including Nl/No), plus _-/, prevents object/budget prefix traps.
    private static bool IdentifierRune(Rune value) => Rune.GetUnicodeCategory(value) is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or
        UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or
        UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber || value.Value is '_' or '-' or '/';
    private static bool ContainsIdentifier(string text, string identifier)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(identifier, start, StringComparison.Ordinal);
            if (index < 0)
                return false;
            var end = index + identifier.Length;
            var before = index == 0 || Rune.DecodeLastFromUtf16(text.AsSpan(0, index), out var prior, out _) != OperationStatus.Done || !IdentifierRune(prior);
            var after = end == text.Length || Rune.DecodeFromUtf16(text.AsSpan(end), out var next, out _) != OperationStatus.Done || !IdentifierRune(next);
            if (before && after)
                return true;
            start = index + 1;
        }
        return false;
    }
    [GeneratedRegex(@"^-?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant)] private static partial Regex DecimalResultPattern();
}
