namespace InsightService.Services.PublicReads;

public sealed class PublicInsightReadException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    internal static PublicInsightReadException Missing() => new(404, "INSIGHT_NOT_FOUND", "Insight not found.");
    internal static PublicInsightReadException Contract(string code = "INSIGHT_READ_CONTRACT") =>
        new(502, code, "The read service returned incompatible data.");
    internal static PublicInsightReadException Unavailable(string code = "INSIGHT_READ_UNAVAILABLE") =>
        new(503, code, "Insight reading is unavailable; no empty result is implied.");
    internal static PublicInsightReadException Invalid() => new(400, "INVALID_REQUEST", "Invalid bounded insight read request.");
}
