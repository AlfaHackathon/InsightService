using InsightService.Contracts.PublicReads;
using InsightService.Services.PublicReads;
using Xunit;

public sealed class InsightConnectionTicketTests
{
    [Fact]
    public async Task TicketIsRandomBoundedAndConsumedAtomicallyOnlyOnce()
    {
        var tickets = new InsightConnectionTickets();
        var user = Guid.NewGuid();
        var membership = new PublicInsightMembership(Guid.NewGuid(), "bank");
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var issued = tickets.Issue(user, membership, expiry);
        Assert.Equal(64, issued.Ticket.Length);
        Assert.All(issued.Ticket, character => Assert.True(char.IsAsciiHexDigitLower(character)));
        Assert.InRange(issued.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(28), DateTimeOffset.UtcNow.AddSeconds(30));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => tickets.Consume(issued.Ticket))));
        var consumed = Assert.Single(attempts, value => value is not null)!;
        Assert.Equal(user, consumed.UserId);
        Assert.Equal(membership, consumed.Membership);
        Assert.Equal(expiry, consumed.ConnectionExpiresAt);
        Assert.Null(tickets.Consume(issued.Ticket));
        Assert.Null(tickets.Consume("not-a-ticket"));
        Assert.Null(tickets.Consume(new string('a', 64)));
    }

    [Fact]
    public async Task TicketCannotOutliveAuthenticationAndOutstandingCredentialsAreBounded()
    {
        var tickets = new InsightConnectionTickets();
        var user = Guid.NewGuid();
        var membership = new PublicInsightMembership(Guid.NewGuid(), null);
        var expiry = DateTimeOffset.UtcNow.AddMilliseconds(20);
        var shortTicket = tickets.Issue(user, membership, expiry);
        Assert.Equal(expiry, shortTicket.ExpiresAt);
        await Task.Delay(40);
        Assert.Null(tickets.Consume(shortTicket.Ticket));
        for (var index = 0; index < 4; index++)
        { tickets.Issue(user, membership, DateTimeOffset.UtcNow.AddMinutes(1)); }
        Assert.Equal(429, Assert.Throws<PublicInsightReadException>(() => tickets.Issue(user, membership, DateTimeOffset.UtcNow.AddMinutes(1))).StatusCode);
        Assert.Equal(401, Assert.Throws<PublicInsightReadException>(() => tickets.Issue(user, membership, DateTimeOffset.UtcNow.AddSeconds(-1))).StatusCode);
    }
}
