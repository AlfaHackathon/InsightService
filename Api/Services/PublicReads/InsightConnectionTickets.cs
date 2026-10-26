using System.Security.Cryptography;
using System.Text;
using InsightService.Contracts.PublicReads;

namespace InsightService.Services.PublicReads;

public sealed record InsightConnectionTicket(Guid UserId, PublicInsightMembership Membership,
    DateTimeOffset ExpiresAt, DateTimeOffset ConnectionExpiresAt);
public sealed record InsightConnectionTicketResponse(string Ticket, DateTimeOffset ExpiresAt, DateTimeOffset ConnectionExpiresAt);

// Process-local, bounded, short-lived credentials. A restart/another replica rejects
// the ticket; clients request a fresh one. No JWT, company body or credentials in URLs.
public sealed class InsightConnectionTickets
{
    public const string Protocol = "insight.v1";
    public const string TicketPrefix = "insight.ticket.";
    private readonly object gate = new();
    private readonly Dictionary<string, InsightConnectionTicket> pending = new(StringComparer.Ordinal);

    public InsightConnectionTicketResponse Issue(Guid user, PublicInsightMembership scope, DateTimeOffset connectionExpires)
    {
        var now = DateTimeOffset.UtcNow;
        if (connectionExpires <= now)
        { throw new PublicInsightReadException(401, "WS_AUTH_REQUIRED", "Current authentication required."); }
        var expires = now.AddSeconds(30);
        if (expires > connectionExpires)
        { expires = connectionExpires; }
        var credential = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        lock (gate)
        {
            foreach (var key in pending.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
            { pending.Remove(key); }
            if (pending.Count >= 1024 || pending.Values.Count(ticket => ticket.UserId == user) >= 4)
            {
                throw new PublicInsightReadException(429, "WS_TICKET_LIMIT", "Retry after existing connection tickets expire.");
            }
            pending.Add(Key(credential), new(user, scope, expires, connectionExpires));
        }
        return new(credential, expires, connectionExpires);
    }

    public InsightConnectionTicket? Consume(string credential)
    {
        if (credential.Length != 64 || credential.Any(character => !char.IsAsciiHexDigitLower(character)))
        { return null; }
        lock (gate)
        {
            return pending.Remove(Key(credential), out var ticket) && ticket.ExpiresAt > DateTimeOffset.UtcNow ? ticket : null;
        }
    }

    private static string Key(string credential) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
}
