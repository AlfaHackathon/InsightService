using System.Text.Json;
using InsightService.Common;
using InsightService.Contracts.Analyses;
using InsightService.Contracts.Context;
using InsightService.Contracts.NewsJobs;
using InsightService.Contracts.Questions;
using Npgsql;

// Deliberately seeded persisted history, not the read query's own projection.
// Every caller owns an isolated PgTestDatabase with the official migrations.
internal static class InsightReadTestData
{
    internal static readonly Guid Company = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid OtherCompany = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-10-25T08:00:00Z");
    internal const string SourceHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    internal const string Hidden = "foreign-company-checkpoint-must-not-leak";

    internal static EffectRevision Revision(string id, string? previous = null, string news = "current-news",
        string status = "unknown", int? importance = null, string mechanism = "liquidity", int daysAgo = 0) =>
        new(id, previous, id + "-news-version", Time.AddDays(-daysAgo), "Synthetic " + id, status,
            [new("own-original", "v1", SourceHash)], null, null, null, Time.AddDays(-daysAgo),
            new("cost", "annual_run_rate", "synthetic-object", "unknown", 5, 5, 5), news,
            new(mechanism, "event_only", "qualitative", "2026-11", null, "qualitative"),
            new([], [new MissingFact("Actual company exposure remains unknown", "company exposure", "company", "own-original", "exposure")]),
            ModelExplanation: "Synthetic bounded explanation", PublicationClass: "missing_data",
            CompanyImportance: importance is null ? null : new(importance, "Synthetic assessed decision importance", [new("own-original", "v1", SourceHash)]));

    internal static async Task Personal(PgTestDatabase database)
    {
        await Head(database, "q-a", Company, true,
            Revision("r-a1", news: "origin-news", status: "supported", importance: 5, daysAgo: 2),
            Revision("r-a2", "r-a1", importance: 4));
        await Head(database, "q-z", Company, true, Revision("r-z"));
        await Head(database, "effect-m", Company, false,
            Revision("r-m1", news: "legacy-news", mechanism: "demand", daysAgo: 3),
            Revision("r-m2", "r-m1", news: "legacy-news", status: "forecast", importance: 2, mechanism: "demand", daysAgo: 1));
        await Head(database, "q-foreign", OtherCompany, true, Revision("r-foreign", importance: 5));
        using var connection = database.Open();
        using var command = new NpgsqlCommand("""
            INSERT INTO question_validity_events(effect_id,material_id,kind,version_id,known_ticks) VALUES
            ('q-a','own-original','invalidated','v2',10),
            ('q-a','own-original','validated','future-v3',@future),
            ('q-z','own-original','invalidated','v2',10),
            ('q-z','own-original','validated','v3',20)
            """, connection);
        command.Parameters.AddWithValue("future", DateTimeOffset.Parse("3000-01-01T00:00:00Z").UtcTicks);
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task Head(PgTestDatabase database, string id, Guid company, bool canonical, params EffectRevision[] revisions)
    {
        var table = canonical ? "questions" : "effects";
        var history = canonical ? "question_revisions" : "effect_revisions";
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = new NpgsqlCommand($"""
            INSERT INTO {table}(effect_id,company_id,bank_profile_id,news_material_id,effect_key,literal_object_ref,current_revision_id)
            VALUES(@id,@company,NULL,@news,@id,@literal,@head)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("company", company.ToString("D"));
            insert.Parameters.AddWithValue("news", revisions[0].NewsMaterialId!);
            insert.Parameters.AddWithValue("literal", "synthetic-contract:" + id);
            insert.Parameters.AddWithValue("head", revisions[^1].RevisionId);
            await insert.ExecuteNonQueryAsync();
        }
        for (var index = 0; index < revisions.Length; index++)
        {
            var revision = revisions[index];
            using var insert = new NpgsqlCommand($"""
                INSERT INTO {history}(revision_id,effect_id,previous_revision_id,news_version_id,sequence,payload_json)
                VALUES(@revision,@id,@previous,@version,@sequence,@payload)
                """, connection, transaction);
            insert.Parameters.AddWithValue("revision", revision.RevisionId);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("previous", NpgsqlTypes.NpgsqlDbType.Text, (object?)revision.PreviousRevisionId ?? DBNull.Value);
            insert.Parameters.AddWithValue("version", revision.NewsVersionId);
            insert.Parameters.AddWithValue("sequence", index + 1);
            insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(revision, AnalysisProtocol.Json));
            await insert.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    internal static async Task Jobs(PgTestDatabase database)
    {
        await Job(database, "old-job", 20, [Company]);
        await Job(database, "permitted-latest", 30, [Company, OtherCompany], count: 2);
        await Job(database, "newer-foreign", 40, [OtherCompany]);
        await Job(database, "partial-job", 35, [Company], news: "second-news", status: "partial");
        await Job(database, "queued-job", 90, [Company], status: "queued");
        await Job(database, "running-job", 80, [Company], status: "running");
        await Job(database, "failed-job", 70, [Company], status: "failed");
    }

    internal static async Task Job(PgTestDatabase database, string id, long order, Guid[] selected, string news = "shared-news",
        string status = "completed", int count = 1, string companyStatus = "completed")
    {
        var request = new NewsJobRequest(id, news, "v1", Time,
            JsonSerializer.SerializeToElement(new { textCompleteness = "lead" }));
        var checkpoint = JsonSerializer.SerializeToElement(new
        {
            selectedCompanyIds = selected,
            pendingCompanyIds = new[] { OtherCompany },
            companies = selected.Select(company => new { companyId = company, status = companyStatus, privateNote = Hidden }),
            roleViews = new[] { new { personId = "foreign-person", privateNote = Hidden } },
            privateNote = Hidden,
            result = new
            {
                generalInsights = Enumerable.Range(0, count).Select(index => new
                {
                    title = id + ":" + index,
                    conclusion = "Synthetic general conclusion",
                    mechanism = "liquidity",
                    applicability = Array.Empty<string>(),
                    exclusions = Array.Empty<string>(),
                    unknowns = new[] { "Company exposure requires its own original" },
                    sourceRefs = new[] { new SourceRef("public-news", "v1", SourceHash) }
                })
            }
        }, AnalysisProtocol.Json);
        var job = new NewsJob(request, status, 1, null, null, checkpoint, null, Time);
        using var connection = database.Open();
        using var insert = new NpgsqlCommand("""
            INSERT INTO news_jobs(job_id,request_hash,status,version,updated_ticks,payload)
            VALUES(@id,'synthetic-read-fixture',@status,1,@order,@payload)
            """, connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("status", status);
        insert.Parameters.AddWithValue("order", order);
        insert.Parameters.AddWithValue("payload", JsonSerializer.Serialize(job, AnalysisProtocol.Json));
        await insert.ExecuteNonQueryAsync();
    }
}
