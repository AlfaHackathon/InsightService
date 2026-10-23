using System.Reflection;
using System.Text.Json;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.NewsJobs;
using InsightService.Models;
using InsightService.Repositories;
using InsightService.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using Xunit;

public sealed class PersistenceRegressionTests : IDisposable
{
    private readonly PgTestDatabase database = new();
    private EffectRepository Effects() => new(database.Context);
    private NewsJobRepository Jobs() => new(database.Context);
    private NpgsqlConnection Open() => database.Open();
    private static void Execute(NpgsqlConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private long Count(string table)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task LegacyInvalidationsAreBackfilledOnceAndSurviveRepeatedReopen()
    {
        var company = Guid.NewGuid();
        var asOf = DateTimeOffset.Parse("2026-10-20T00:00:00Z");
        using (var connection = Open())
        {
            Execute(connection, "DROP TABLE question_validity_events; DELETE FROM \"VersionInfo\"");
            Execute(connection, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pre-validity-events.sql")));
            Execute(connection, $"INSERT INTO questions VALUES('question_legacy','{company:D}',NULL,'news','legacy-key','contract','revision_legacy')");
            var revision = new EffectRevision("revision_legacy", null, "v1", asOf, "Historical conclusion", "unknown", [], null, null, null, asOf);
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO question_revisions VALUES('revision_legacy','question_legacy',NULL,'v1',1,@json)";
            insert.Parameters.AddWithValue("@json", JsonSerializer.Serialize(revision, AnalysisService.Json));
            insert.ExecuteNonQuery();
            Execute(connection, "INSERT INTO question_invalidations VALUES('question_legacy','contract','v2')");
            Execute(connection, $"INSERT INTO question_invalidation_times VALUES('question_legacy','contract',{asOf.AddHours(1).UtcTicks})");
        }
        for (var reopen = 0; reopen < 3; reopen++)
        {
            database.Migrate();
            var repository = Effects();
            var stored = (await repository.GetEffect("question_legacy"))!;
            Assert.True(stored.RequiresReassessment);
            Assert.Equal("Historical conclusion", stored.CurrentRevision.Conclusion);
            Assert.Single(stored.History);
            Assert.False((await repository.GetSnapshot(company, "news", asOf)).Effects.Single().RequiresReassessment);
            Assert.True((await repository.GetSnapshot(company, "news", asOf.AddHours(2))).Effects.Single().RequiresReassessment);
            Assert.Equal(1, Count("question_validity_events"));
        }
    }

    [Fact]
    public async Task FailureAfterFirstDraftRollsBackRevisionHeadAndReceiptTogether()
    {
        var repository = Effects();
        var company = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var firstDraft = new EffectDraft("existing", "contract", null, "first", "unknown", []);
        var initial = new AnalysisRequest(company, null, "news", "v1", now, [firstDraft]);
        var saved = (await repository.Save(initial, "hash-v1", [new(firstDraft, null)]));
        var newDraft = firstDraft with
        {
            EffectKey = "brand-new"
        };
        var staleDraft = firstDraft with
        {
            ExpectedPreviousRevisionId = "stale-revision"
        };
        var correction = initial with
        {
            NewsVersionId = "v2",
            Drafts = [newDraft, staleDraft]
        };
        var error = await Assert.ThrowsAsync<ApiException>(async () => await repository.Save(correction, "hash-v2", [new(newDraft, null), new(staleDraft, null)]));
        Assert.Equal("stale_previous_revision", error.Code);
        var reopened = Effects();
        Assert.Null((await reopened.FindAnalysis(company, "news", "v2")));
        var effect = Assert.Single((await reopened.GetSnapshot(company, "news")).Effects);
        Assert.Equal(saved.Effects.Single().RevisionId, effect.CurrentRevision.RevisionId);
        Assert.Single(effect.History);
        Assert.Equal(1, Count("effects"));
        Assert.Equal(1, Count("effect_revisions"));
        Assert.Equal(1, Count("analysis_receipts"));
    }

    [Fact]
    public async Task FailedAuditInsertRollsBackJobAndAllowsCleanRetry()
    {
        var store = Jobs();
        using (var connection = Open())
        {
            Execute(connection, "CREATE FUNCTION reject_job_audit() RETURNS trigger LANGUAGE plpgsql AS $audit$ BEGIN RAISE EXCEPTION 'forced audit failure'; END $audit$; CREATE TRIGGER reject_job_audit BEFORE INSERT ON news_job_events FOR EACH ROW EXECUTE FUNCTION reject_job_audit()");
        }

        var request = new NewsJobRequest("rollback-job", "news", "v1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new
        {
        }));
        await Assert.ThrowsAsync<PostgresException>(async () => await store.Create(request));
        Assert.Equal(0, Count("news_jobs"));
        Assert.Equal(0, Count("news_job_events"));
        using (var connection = Open())
        {
            Execute(connection, "DROP TRIGGER reject_job_audit ON news_job_events");
        }

        Assert.Equal("queued", (await Jobs().Create(request)).Status);
        Assert.Equal(1, Count("news_jobs"));
        Assert.Equal(1, Count("news_job_events"));
    }

    [Fact]
    public async Task SimultaneousWorkersHaveOneLeaseAndStaleOwnerCannotCommit()
    {
        var first = Jobs();
        var second = Jobs();
        await first.Create(new("race-job", "news", "v1", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new
        {
        })));
        using var gate = new ManualResetEventSlim();
        var attempts = new[] { first, second }.Select((store, i) => Task.Run(async () =>
        {
            gate.Wait();
            return (await store.Claim(new("worker-" + i, "race-job")));
        })).ToArray();
        gate.Set();
        var claims = await Task.WhenAll(attempts);
        var winner = Assert.Single(claims, job => job is not null)!;
        var current = (await Jobs().GetAsync("race-job"));
        Assert.Equal(winner.WorkerId, current.WorkerId);
        Assert.Equal(1, current.Version);
        var wrongWorker = winner.WorkerId == "worker-0" ? "worker-1" : "worker-0";
        Assert.Equal("job_lease_conflict", (await Assert.ThrowsAsync<ApiException>(async () => await Jobs().Update("race-job", new(wrongWorker, winner.Version, "completed", winner.Checkpoint)))).Code);
        Assert.Equal("running", (await Jobs().GetAsync("race-job")).Status);
        Assert.Equal(2, Count("news_job_events"));
    }

    [Fact]
    public void EmbeddedSqlAccessorsResolveAllStatementsAndFailFastForUnknownNames()
    {
        var assembly = typeof(EffectRepository).Assembly;
        Assert.Contains("InsightService.Resources.InsightSql.resources", assembly.GetManifestResourceNames());
        var type = assembly.GetType("InsightService.Resources.InsightSql", throwOnError: true)!;
        var properties = type.GetProperties(BindingFlags.Static | BindingFlags.NonPublic);
        Assert.True(properties.Length >= 40);
        foreach (var property in properties)
        {
            var sql = Assert.IsType<string>(property.GetValue(null));
            Assert.False(string.IsNullOrWhiteSpace(sql), property.Name);
            Assert.DoesNotContain("{stateTable}", sql);
            Assert.DoesNotContain("{revisionTable}", sql);
        }
        var required = type.GetMethod("Required", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => required.Invoke(null, ["AbsentSqlStatement"]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("AbsentSqlStatement", error.InnerException!.Message);
    }

    public void Dispose() => database.Dispose();
    private sealed class EnvironmentStub(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "test";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
