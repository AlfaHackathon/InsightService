using AlfaHackaton.HttpClient.Common.Settings;
namespace InsightService.Settings;

public sealed class ContextHttpSettings : HttpClientSettings
{
    public ContextHttpSettings()
    {
        Timeout = TimeSpan.FromSeconds(15);
        AutoRedirect = false;
        IgnoreVerifySsl = false;
        JsonSettings = new Newtonsoft.Json.JsonSerializerSettings { MaxDepth = 64 };
    }
}
