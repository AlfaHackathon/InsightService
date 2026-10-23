using FluentMigrator;
using InsightService.Resources;
namespace InsightService.Migrations;

[Migration(2026102301)]
public sealed class InitialInsightSchema : Migration
{
    public override void Up()
    {
        Execute.Sql(InsightSql.CreateUtcTicks);
        Execute.Sql(InsightSql.InitializeEffects);
        Execute.Sql(InsightSql.InitializeNewsJobs);
    }
    public override void Down() => throw new NotSupportedException("Insight history requires an explicit data migration; destructive rollback is disabled.");
}
