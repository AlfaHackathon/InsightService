using InsightService.Contracts.PublicReads;
using InsightService.Middleware;
using InsightService.Services.PublicReads;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[Route("insight-service/insights")]
public sealed class PublicInsightReadsController(PublicInsightReadService reads) : ControllerBase
{
    private Guid GatewayUserId => PublicInsightReadMiddleware.UserId(HttpContext);
    private void Query(params string[] allowed)
    {
        if (Request.Query.Any(pair => pair.Value.Count != 1 || !allowed.Contains(pair.Key, StringComparer.Ordinal)))
        {
            throw new PublicInsightReadException(400, "INVALID_REQUEST", "Invalid insight read request.");
        }
    }
    private string? Raw(string key, string? fallback) => Request.Query.TryGetValue(key, out var values) ? values[0] : fallback;
    private static void Cursor(string? cursor)
    {
        if (cursor is not null && string.IsNullOrWhiteSpace(cursor))
        {
            throw new PublicInsightReadException(400, "INVALID_CURSOR", "Invalid read cursor or filters.");
        }
    }
    private IActionResult ReadResult(object value)
    {
        if (HttpMethods.IsHead(Request.Method))
        {
            Response.ContentType = "application/json; charset=utf-8";
            return StatusCode(StatusCodes.Status200OK);
        }
        return Ok(value);
    }
    [HttpGet]
    [HttpHead]
    [ProducesResponseType(typeof(PublicInsightFeed), StatusCodes.Status200OK)]
    public async Task<IActionResult> Feed([FromQuery] PublicInsightFeedQuery query, CancellationToken ct)
    {
        Query("kind", "limit", "cursor", "status", "mechanism", "newsMaterialId", "minImportance", "category");
        // MVC converts empty/whitespace strings to null; preserve literal filters so
        // an explicitly supplied filter can never silently become an unfiltered feed.
        query.Kind = Raw("kind", query.Kind)!;
        query.Status = Raw("status", query.Status);
        query.Category = Raw("category", query.Category);
        query.Mechanism = Raw("mechanism", query.Mechanism);
        query.NewsMaterialId = Raw("newsMaterialId", query.NewsMaterialId);
        query.Cursor = Raw("cursor", query.Cursor);
        Cursor(query.Cursor);
        if (Request.Query.TryGetValue("minImportance", out var importance) && string.IsNullOrWhiteSpace(importance[0]))
        {
            throw new PublicInsightReadException(400, "INVALID_REQUEST", "Invalid insight read request.");
        }

        return ReadResult(await reads.Feed(GatewayUserId, query, ct));
    }
    [HttpGet("personal/{id}/history")]
    [HttpHead("personal/{id}/history")]
    [ProducesResponseType(typeof(PublicInsightHistory), StatusCodes.Status200OK)]
    public async Task<IActionResult> History(string id, [FromQuery] PublicInsightHistoryQuery query, CancellationToken ct)
    {
        Query("limit", "cursor");
        query.Cursor = Raw("cursor", query.Cursor);
        Cursor(query.Cursor);
        return ReadResult(await reads.History(GatewayUserId, id, query, ct));
    }
    [HttpGet("{kind}/{id}")]
    [HttpHead("{kind}/{id}")]
    [ProducesResponseType(typeof(PublicInsightCard), StatusCodes.Status200OK)]
    public async Task<IActionResult> Detail(string kind, string id, CancellationToken ct)
    {
        Query();
        return ReadResult(await reads.Detail(GatewayUserId, kind, id, ct));
    }
}
