using System.Globalization;
using System.Numerics;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Projections;
using InsightService.Contracts.Questions;

namespace InsightService.Models.Policies;

public static class ProjectionPolicy
{
    public static int Priority(EffectRevision revision)
    {
        if (revision.Impact is not { } p)
        {
            return 0;
        }

        var confidence = p.EvidenceStatus == "supported" ? 1m : p.EvidenceStatus == "conditional" ? 0.6m : 0.2m;
        return (int)decimal.Round(100 * confidence * (0.5m * p.Importance + 0.3m * p.Urgency + 0.2m * p.Novelty) / 5, 0, MidpointRounding.AwayFromZero);
    }

    public static GroupProjection Group(GroupExpansion group, CompanyProjection[] companies)
    {
        var runs = companies.ToDictionary(c => c.CompanyId);
        var missing = group.CompanyIds.Where(id => !runs.TryGetValue(id, out var c) || c.Status != "completed").ToArray();
        var partial = group.CompanyIds.Where(id => !runs.TryGetValue(id, out var c) || c.ContextComplete != true).ToArray();
        var unrevised = group.CompanyIds.SelectMany(id => runs.TryGetValue(id, out var c) ? (c.Receipt?.UnrevisedEffectIds ?? []).Concat(c.StaleEffectIds ?? []) : []).ToArray();
        var empty = group.CompanyIds.Where(id => runs.TryGetValue(id, out var c) && c.Receipt?.Effects.Count == 0).ToArray();
        var revisions = companies.SelectMany(c => c.Receipt?.Effects ?? []).DistinctBy(e => e.EffectId).ToDictionary(e => e.EffectId, e => e.CurrentRevision, StringComparer.Ordinal);
        var frames = new Dictionary<FrameIdentity, Frame>();
        var unknown = new List<string>();
        var duplicates = new List<string>();
        foreach (var id in group.CompanyIds.Distinct())
        {
            if (!runs.TryGetValue(id, out var run) || run.Receipt is not { } receipt)
            {
                continue;
            }

            foreach (var saved in receipt.Effects)
            {
                if (run.StaleEffectIds?.Contains(saved.EffectId, StringComparer.Ordinal) == true)
                {
                    unknown.Add(saved.EffectId);
                    continue;
                }
                var revision = saved.CurrentRevision;
                var impact = revision.Impact;
                if (impact?.EvidenceStatus != "supported" || revision.Calculation is not { } calculation || revision.CalculationResult is null)
                {
                    if (impact?.EvidenceStatus != "unaffected")
                    {
                        unknown.Add(saved.EffectId);
                    }

                    continue;
                }
                var q = revision.Question;
                var key = new FrameIdentity(q?.Mechanism, q?.Frame, q?.BaselinePeriod, q?.Basis, impact.Metric, impact.AmountBasis, calculation.Unit, calculation.Period);
                if (!frames.TryGetValue(key, out var frame))
                {
                    frames[key] = frame = new(key, q);
                }

                if (impact.EconomicObjectId is { } economicObject && !frame.Objects.Add(economicObject))
                {
                    duplicates.Add(saved.EffectId);
                    continue;
                }
                frame.Values.Add(revision.CalculationResult);
                frame.Effects.Add(saved.EffectId);
                frame.Companies.Add(id);
            }
        }
        var confirmed = group.AggregationRule == "sum_compatible_owner_effects";
        var totals = confirmed ? frames.Values.Select(frame =>
        {
            var unresolved = unknown.Concat(unrevised).Distinct(StringComparer.Ordinal).Where(id => Related(revisions.GetValueOrDefault(id)?.Question, frame.Question)).ToArray();
            var duplicateFrame = duplicates.Where(id => Related(revisions.GetValueOrDefault(id)?.Question, frame.Question)).ToArray();
            var absent = group.CompanyIds.Where(id => !frame.Companies.Contains(id) && (!runs.TryGetValue(id, out var run) || run.Receipt?.Effects.Any(e => SameFrameUnaffected(e.CurrentRevision, frame)) != true)).ToArray();
            var q = frame.Question;
            var complete = group.Complete && missing.Length == 0 && partial.Length == 0 && empty.Length == 0 && unresolved.Length == 0 && duplicateFrame.Length == 0 && absent.Length == 0 &&
                (q is null || q.Basis is not (null or "unknown") && q.Unit != "unknown" && q.TargetPeriod != "unknown" && q.BaselinePeriod != "unknown");
            var sum = Sum(frame.Values);
            return new GroupTotal
            {
                Metric = frame.Identity.Metric,
                AmountBasis = frame.Identity.AmountBasis,
                Unit = frame.Identity.Unit,
                Period = frame.Identity.Period,
                Question = q,
                KnownSubtotal = sum,
                FullTotal = complete ? sum : null,
                Coverage = new() { Complete = complete, MissingCompanyIds = absent, UnresolvedEffectIds = unresolved, DuplicateObjects = duplicateFrame },
                EffectIds = frame.Effects.ToArray(),
                CompanyIds = frame.Companies.Distinct().ToArray()
            };
        }).ToArray() : [];
        var coverageComplete = group.Complete && confirmed && partial.Length == 0 && missing.Length == 0 && unknown.Count == 0 && duplicates.Count == 0 && unrevised.Length == 0 && empty.Length == 0;
        return new()
        {
            GroupId = group.GroupId,
            CompanyIds = group.CompanyIds.Distinct().ToArray(),
            Complete = coverageComplete && totals.All(t => t.Coverage.Complete),
            Coverage = new()
            {
                ExpectedCompanies = group.CompanyIds.Length,
                AnalyzedCompanies = group.CompanyIds.Length - missing.Length,
                MissingCompanyIds = missing,
                MissingGroups = group.MissingGroups,
                Cycles = group.Cycles,
                UnknownEffects = unknown.ToArray(),
                DuplicateObjects = duplicates.ToArray(),
                UnrevisedEffectIds = unrevised,
                EmptyCompanyIds = empty,
                PartialContextCompanyIds = partial,
                AggregationRuleConfirmed = confirmed
            },
            Totals = totals,
            SourceRefs = group.SourceRefs
        };
    }

    private static bool Related(QuestionIdentity? candidate, QuestionIdentity? frame) => candidate is null || frame is null ||
        candidate.Mechanism == frame.Mechanism && candidate.Frame == frame.Frame && (candidate.TargetPeriod == "unknown" || candidate.TargetPeriod == frame.TargetPeriod) &&
        (candidate.Unit == "unknown" || candidate.Unit == frame.Unit) && (candidate.BaselinePeriod == "unknown" || candidate.BaselinePeriod == frame.BaselinePeriod) &&
        (candidate.Basis is null or "unknown" || candidate.Basis == frame.Basis);

    private static bool SameFrameUnaffected(EffectRevision revision, Frame frame) => revision.Impact?.EvidenceStatus == "unaffected" &&
        (frame.Question is { } q ? revision.Question == q : revision.Question is null && revision.Calculation?.Unit == frame.Identity.Unit &&
        revision.Calculation.Period == frame.Identity.Period && revision.Impact.Metric == frame.Identity.Metric && revision.Impact.AmountBasis == frame.Identity.AmountBasis);

    private static string Sum(List<string> values)
    {
        var parts = values.Select(value => { var split = value.Split('.'); return (Digits: BigInteger.Parse(string.Concat(split), CultureInfo.InvariantCulture), Scale: split.Length == 2 ? split[1].Length : 0); }).ToArray();
        var scale = parts.Max(p => p.Scale);
        var sum = parts.Aggregate(BigInteger.Zero, (total, p) => total + p.Digits * BigInteger.Pow(10, scale - p.Scale));
        var digits = BigInteger.Abs(sum).ToString(CultureInfo.InvariantCulture).PadLeft(scale + 1, '0');
        return (sum.Sign < 0 ? "-" : "") + (scale == 0 ? digits : (digits[..^scale] + "." + digits[^scale..]).TrimEnd('0').TrimEnd('.'));
    }

    private sealed record FrameIdentity(string? Mechanism, string? Frame, string? Baseline, string? Basis, string Metric, string AmountBasis, string Unit, string Period);
    private sealed class Frame(FrameIdentity identity, QuestionIdentity? question)
    {
        public FrameIdentity Identity { get; } = identity;
        public QuestionIdentity? Question { get; } = question;
        public List<string> Values { get; } = [];
        public List<string> Effects { get; } = [];
        public List<Guid> Companies { get; } = [];
        public HashSet<string> Objects { get; } = new(StringComparer.Ordinal);
    }
}
