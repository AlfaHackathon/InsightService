using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AlfaHackaton.Result.Errors;
using InsightService.Contracts.Analyses;

namespace InsightService.Models.Policies;

internal static partial class CalculationClaimPolicy
{
    // Intentionally narrow: one signed decimal result followed by a literal unit.
    // Arbitrary prose, worded amounts, scales and financial truth are not inferred.
    [GeneratedRegex(@"^(?<value>[+-]?(?:[0-9]+|[0-9]{1,3}(?:[ \u00a0\u202f][0-9]{3})+)(?:\.[0-9]+)?)[ \u00a0\u202f]*(?<unit>[A-Z]{3}|₽|руб\.)$", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
    [GeneratedRegex(@"[+-]?[0-9]+(?:[ \u00a0\u202f][0-9]{3})*(?:\.[0-9]+)?", RegexOptions.CultureInvariant)]
    private static partial Regex Number();
    [GeneratedRegex(@"₽|(?<![\p{L}])[A-Z]{3}(?![\p{L}])|(?<![\p{L}])руб(?:\.|лей|ля|ль)?(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex UnitMarker();

    [GeneratedRegex(@"(?i)(?<![\p{L}])(?:тыс(?:яч[а-я]*)?|млн|миллион[а-я]*|млрд|миллиард[а-я]*|трлн|триллион[а-я]*|thousand|million|billion|trillion)(?![\p{L}])", RegexOptions.CultureInvariant)]
    private static partial Regex ScaleMarker();

    public static Error? Validate(EffectDraft draft, string? result)
    {
        var claim = draft.CalculationClaim;
        if (draft.Calculation is not { } calculation)
        {
            return claim is null ? null : Fail("CONCLUSION_CLAIM_WITHOUT_CALCULATION", "A result claim requires this draft's calculation.");
        }

        var remaining = draft.Conclusion;
        if (claim is not null)
        {
            if (string.IsNullOrEmpty(claim.Quote) || claim.Quote.Length > 200 || claim.Quote.Any(char.IsControl) ||
                string.IsNullOrEmpty(claim.Value) || claim.Value.Length > 100 ||
                string.IsNullOrEmpty(claim.Unit) || claim.Unit.Length > 64 ||
                string.IsNullOrEmpty(claim.Period) || claim.Period.Length > 1000)
            {
                return Fail("CONCLUSION_CLAIM_INVALID_QUOTE", "Use one bounded decimal-result quote.");
            }
            var literal = Literal().Match(claim.Quote);
            var index = remaining.IndexOf(claim.Quote, StringComparison.Ordinal);
            if (!literal.Success || index < 0 || remaining.IndexOf(claim.Quote, index + claim.Quote.Length, StringComparison.Ordinal) >= 0 ||
                index > 0 && (IsLetterOrDigitAt(remaining, index - 1) || remaining[index - 1] is '.' or ',' or '+' or '-' or '−') ||
                index + claim.Quote.Length < remaining.Length && IsLetterOrDigitAt(remaining, index + claim.Quote.Length))
            {
                return Fail("CONCLUSION_CLAIM_INVALID_QUOTE", "Quote must be one exact complete decimal-and-unit occurrence in conclusion.");
            }
            if (Canonical(literal.Groups["value"].Value) != claim.Value || claim.Value != result)
            {
                return Fail("CONCLUSION_CLAIM_VALUE_MISMATCH", "Quoted value must equal the canonical calculation result.");
            }
            var unit = literal.Groups["unit"].Value;
            unit = unit is "₽" or "руб." ? "RUB" : unit;
            if (unit != claim.Unit || claim.Unit != calculation.Unit)
            {
                return Fail("CONCLUSION_CLAIM_UNIT_MISMATCH", "Quoted unit must equal the calculation unit.");
            }
            if (claim.Period != calculation.Period)
            {
                return Fail("CONCLUSION_CLAIM_PERIOD_MISMATCH", "Claim period must equal the calculation period.");
            }
            remaining = remaining.Remove(index, claim.Quote.Length).Insert(index, " ");
        }

        if (UnitMarker().IsMatch(remaining) || ScaleMarker().IsMatch(remaining))
        {
            return Fail("CONCLUSION_CLAIM_REQUIRED", "Put the result in one exact supported quote, or keep conclusion qualitative.");
        }
        // Declared period is allowed as text; other numerical facts belong in the
        // structured calculation/assessment, not an unbound result assertion.
        remaining = Regex.Replace(remaining, @"(?<![\p{L}\p{N}])" + Regex.Escape(calculation.Period) + @"(?![\p{L}\p{N}])", " ", RegexOptions.CultureInvariant);
        if (Number().IsMatch(remaining))
        {
            return Fail(claim is null ? "CONCLUSION_CLAIM_REQUIRED" : "CONCLUSION_UNBOUND_NUMBER", "Only the bound result and declared period may be numeric in calculated conclusion.");
        }
        return null;
    }

    private static bool IsLetterOrDigitAt(string value, int index)
    {
        if (index > 0 && char.IsLowSurrogate(value[index]) && char.IsHighSurrogate(value[index - 1]))
        {
            index--;
        }
        return Rune.TryGetRuneAt(value, index, out var rune) && Rune.IsLetterOrDigit(rune);
    }
    private static string Canonical(string value)
    {
        value = value.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "");
        var negative = value.StartsWith('-');
        value = value.TrimStart('+', '-');
        var parts = value.Split('.');
        var whole = parts[0].TrimStart('0');
        whole = whole.Length == 0 ? "0" : whole;
        var fraction = parts.Length == 2 ? parts[1].TrimEnd('0') : "";
        var canonical = whole + (fraction.Length == 0 ? "" : "." + fraction);
        return negative && canonical != "0" ? "-" + canonical : canonical;
    }
    private static Error Fail(string code, string reason) => new($"{code}: {reason}", ErrorSource.Client, ErrorType.CorruptedInfo)
    { HttpStatusCode = HttpStatusCode.UnprocessableEntity };
}
