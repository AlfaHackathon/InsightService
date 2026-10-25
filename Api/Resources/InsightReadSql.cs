using System.Resources;
namespace InsightService.Resources;
internal static class InsightReadSql
{
    private static readonly ResourceManager Manager = new("InsightService.Resources.InsightReadSql", typeof(InsightReadSql).Assembly);
    private static string Required(string name) => Manager.GetString(name, System.Globalization.CultureInfo.InvariantCulture) ?? throw new InvalidOperationException($"Missing SQL resource: {name}");
    internal static string ReadTransaction => Required(nameof(ReadTransaction));
    internal static string PersonalPage => Required(nameof(PersonalPage));
    internal static string PersonalDetail => Required(nameof(PersonalDetail));
    internal static string PersonalHistory => Required(nameof(PersonalHistory));
    internal static string GeneralPage => Required(nameof(GeneralPage));
    internal static string GeneralDetail => Required(nameof(GeneralDetail));
}
