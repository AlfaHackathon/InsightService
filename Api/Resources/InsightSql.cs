
using System.Resources;
namespace InsightService.Resources;

/// <summary>SQL is embedded so deployment never depends on loose source files.</summary>
internal static class InsightSql
{
    private static readonly ResourceManager Manager = new("InsightService.Resources.InsightSql", typeof(InsightSql).Assembly);
    private static string Required(string name) => Manager.GetString(name, System.Globalization.CultureInfo.InvariantCulture) ?? throw new InvalidOperationException($"Missing SQL resource: {name}");
    internal static string ReadQuestionHead => Required(nameof(ReadQuestionHead));
    internal static string ReadEffectHead => Required(nameof(ReadEffectHead));
    internal static string ReadQuestionHistory => Required(nameof(ReadQuestionHistory));
    internal static string ReadEffectHistory => Required(nameof(ReadEffectHistory));
    internal static string HasPendingInvalidation => Required(nameof(HasPendingInvalidation));
    internal static string InitializeNewsJobs => Required(nameof(InitializeNewsJobs));
    internal static string FindJobRequest => Required(nameof(FindJobRequest));
    internal static string ReadSourceCursor => Required(nameof(ReadSourceCursor));
    internal static string ReadSourceCursorVersion => Required(nameof(ReadSourceCursorVersion));
    internal static string UpsertSourceCursor => Required(nameof(UpsertSourceCursor));
    internal static string FindClaimableJob => Required(nameof(FindClaimableJob));
    internal static string FindClaimableJobById => Required(nameof(FindClaimableJobById));
    internal static string ReadJob => Required(nameof(ReadJob));
    internal static string InsertJob => Required(nameof(InsertJob));
    internal static string UpdateJob => Required(nameof(UpdateJob));
    internal static string InsertJobEvent => Required(nameof(InsertJobEvent));
    internal static string FindAffectedQuestions => Required(nameof(FindAffectedQuestions));
    internal static string ReadAnalysisWatch => Required(nameof(ReadAnalysisWatch));
    internal static string UpsertQuestionInvalidation => Required(nameof(UpsertQuestionInvalidation));
    internal static string UpsertInvalidationTime => Required(nameof(UpsertInvalidationTime));
    internal static string InsertInvalidationEvent => Required(nameof(InsertInvalidationEvent));
    internal static string InitializeEffects => Required(nameof(InitializeEffects));
    internal static string FindSnapshotEffects => Required(nameof(FindSnapshotEffects));
    internal static string FindPreviousQuestion => Required(nameof(FindPreviousQuestion));
    internal static string RekeyQuestion => Required(nameof(RekeyQuestion));
    internal static string InsertQuestion => Required(nameof(InsertQuestion));
    internal static string InsertEffect => Required(nameof(InsertEffect));
    internal static string InsertQuestionRevision => Required(nameof(InsertQuestionRevision));
    internal static string InsertEffectRevision => Required(nameof(InsertEffectRevision));
    internal static string AdvanceQuestionHead => Required(nameof(AdvanceQuestionHead));
    internal static string AdvanceEffectHead => Required(nameof(AdvanceEffectHead));
    internal static string DeleteQuestionDependencies => Required(nameof(DeleteQuestionDependencies));
    internal static string InsertQuestionDependency => Required(nameof(InsertQuestionDependency));
    internal static string ClearQuestionInvalidation => Required(nameof(ClearQuestionInvalidation));
    internal static string InsertClearedValidityEvent => Required(nameof(InsertClearedValidityEvent));
    internal static string ClearInvalidationTime => Required(nameof(ClearInvalidationTime));
    internal static string FindUnrevisedEffects => Required(nameof(FindUnrevisedEffects));
    internal static string InsertAnalysisReceipt => Required(nameof(InsertAnalysisReceipt));
    internal static string ReadCompanyStateVersion => Required(nameof(ReadCompanyStateVersion));
    internal static string FindQuestionRecipients => Required(nameof(FindQuestionRecipients));
    internal static string FindAnalysisReceipt => Required(nameof(FindAnalysisReceipt));
    internal static string TransactionLock => Required(nameof(TransactionLock));
    internal static string CreateUtcTicks => Required(nameof(CreateUtcTicks));
}
