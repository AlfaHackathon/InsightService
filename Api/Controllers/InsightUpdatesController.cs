using System.Globalization;
using InsightService.Middleware;
using InsightService.Services.PublicReads;
using Microsoft.AspNetCore.Mvc;

namespace InsightService.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public sealed class InsightUpdatesController(InsightUpdateSnapshot snapshots, InsightUpdateSocket sockets,
    InsightSocketLimits limits, InsightConnectionTickets tickets) : ControllerBase
{
    public const string Path = "/insight-service/insights/ws";
    public const string TicketPath = "/insight-service/insights/ws-ticket";
    public const string ExpiryHeader = "X-Insight-Ws-Expires";

    [HttpPost(TicketPath)]
    public async Task<IActionResult> Ticket(CancellationToken ct)
    {
        var user = PublicInsightReadMiddleware.UserId(HttpContext);
        var raw = Request.Headers[ExpiryHeader];
        if (raw.Count != 1 || !long.TryParse(raw[0], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() || seconds > DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds())
        {
            return Unauthorized(new { code = "WS_AUTH_REQUIRED", message = "Expiring Gateway identity required." });
        }
        if (Request.Query.Count != 0 || Request.ContentLength is > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return BadRequest(new { code = "INVALID_REQUEST", message = "Ticket requests have no query or body." });
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(limits.ReadTimeout);
        var scope = await snapshots.Scope(user, budget.Token);
        return Ok(tickets.Issue(user, scope, DateTimeOffset.FromUnixTimeSeconds(seconds)));
    }

    [HttpGet(Path)]
    public async Task<IActionResult> Connect(CancellationToken ct)
    {
        if (Request.Query.Count != 0 || !HttpContext.WebSockets.IsWebSocketRequest)
        {
            return BadRequest(new { code = "WS_UPGRADE_REQUIRED", message = "WebSocket upgrade without query parameters required." });
        }
        // Browser origins must match the public Gateway Host. The Gateway repeats
        // this check before proxying. No new cross-origin allowlist is introduced.
        var origin = Request.Headers.Origin;
        if (origin.Count > 1 || origin.Count == 1 &&
            (!Uri.TryCreate(origin[0], UriKind.Absolute, out var uri) || uri.Authority != Request.Host.Value ||
             uri.Scheme is not ("http" or "https")))
        {
            return StatusCode(403);
        }
        var protocols = HttpContext.WebSockets.WebSocketRequestedProtocols;
        if (protocols.Count != 2 || !protocols.Contains(InsightConnectionTickets.Protocol, StringComparer.Ordinal))
        {
            return Unauthorized(new { code = "WS_TICKET_REQUIRED" });
        }
        var credential = protocols.SingleOrDefault(protocol => protocol.StartsWith(InsightConnectionTickets.TicketPrefix, StringComparison.Ordinal));
        var ticket = credential is null ? null : tickets.Consume(credential[InsightConnectionTickets.TicketPrefix.Length..]);
        if (ticket is null)
        { return Unauthorized(new { code = "WS_TICKET_INVALID" }); }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(limits.ReadTimeout);
        var user = ticket.UserId;
        var scope = ticket.Membership;
        var baseline = await snapshots.Read(user, scope, budget.Token);
        var expires = ticket.ConnectionExpiresAt;
        if (expires <= DateTimeOffset.UtcNow)
        { return Unauthorized(); }
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync(InsightConnectionTickets.Protocol);
        await sockets.Run(socket, user, scope, expires, baseline, ct);
        return new EmptyResult();
    }
}
