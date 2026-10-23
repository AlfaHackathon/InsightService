using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Questions;
using InsightService.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static InsightService.Common.AnalysisProtocol;
namespace InsightService.Models.Policies;

internal static class QuestionIdentityPolicy
{
    public static QuestionIdentity NormalizeQuestion(QuestionIdentity question) =>
        question.Frame == "event_only" && question.Unit == "qualitative"
            ? question with { TargetPeriod = "unknown", BaselinePeriod = null, Basis = "qualitative" }
            : question;

    public static string QuestionKey(string literal, QuestionIdentity input)
    {
        return RawQuestionKey(literal, NormalizeQuestion(input));
    }

    internal static string RawQuestionKey(string literal, QuestionIdentity question)
    {
        if (question.Mechanism is not ("financing_cost" or "refinancing_access" or "lease_cost" or "rent_indexation" or "demand" or "input_cost" or "investment" or "liquidity" or "tax" or "supply_capacity" or "labor_cost" or "deposit_income")
            || question.Frame is not ("event_only" or "absolute" or "budget_delta")
            || !IsPeriod(question.TargetPeriod) || question.BaselinePeriod is not null && !IsPeriod(question.BaselinePeriod)
            || question.Basis is not (null or "qualitative" or "monthly_run_rate" or "annual_run_rate" or "contract_total" or "actual_cashflow" or "budget_total" or "unknown")
            || new[] { literal, question.Unit, question.TargetPeriod }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 1000 || s.Any(char.IsControl))
            || question.BaselinePeriod is { } baseline && (string.IsNullOrWhiteSpace(baseline) || baseline.Length > 1000 || baseline.Any(char.IsControl))
            || question.Frame == "budget_delta" && question.BaselinePeriod is null)
            throw new ApiException(400, "invalid_question_identity", "A stable mechanism, frame, unit, target period and comparison baseline for budget deltas are required.");
        var key = string.Join('\u001f', literal, question.Mechanism, question.Frame, question.Unit, question.TargetPeriod, question.BaselinePeriod ?? "", question.Basis ?? "unknown");
        return "question-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..40];
    }
    public static string ProcessingVersion(string newsVersionId, MaterialTrigger? trigger) => trigger is null ? newsVersionId :
        "trigger-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { newsVersionId, trigger.MaterialId, trigger.VersionId }, Json))));
    private static bool IsPeriod(string? value) => value is not null && Regex.IsMatch(value,
        @"^(?:unknown|(?:(?:from|budget|actual|previous):)?\d{4}(?:-(?:Q[1-4]|(?:0[1-9]|1[0-2])(?:-(?:0[1-9]|[12]\d|3[01]))?))?)$", RegexOptions.CultureInvariant);
    public static string CalculatedConclusion(string frame, string value, string unit, string period, string literal) =>
        $"{(frame == "budget_delta" ? "Расчётное отклонение от бюджета (новое значение минус baseline)" : frame == "event_only" ? "Расчётное изменение от события" : "Расчётное абсолютное значение")}: {value} {unit}; период {period}; объект {literal}. Применимость определяется сохранёнными условиями и статусом основания. Расчёт не подтверждает фактическую оплату.";
}
