using FluentMigrator;
using InsightService.Resources;
namespace InsightService.Migrations;

[Migration(2026102401)]
public sealed class NamedSourceCursors : Migration
{
    public override void Up() => Execute.Sql(InsightSql.InitializeNamedSourceCursors);
    public override void Down() => throw new NotSupportedException("Source checkpoints require an explicit data migration.");
}
