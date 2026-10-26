using InsightService.Services.PublicReads;

namespace InsightService.Middleware;

// AuthGateway validates Bearer and replaces this header. The service must stay private
// behind that boundary; accepting the header does not validate a directly supplied JWT.
public static class PublicInsightReadMiddleware
{
    private const string Identity = "InsightService.PublicRead.UserId";
    public static bool IsPublicInsightRead(PathString path) => path.StartsWithSegments("/insight-service/insights");
    public static Guid UserId(HttpContext context) => context.Items.TryGetValue(Identity, out var value) && value is Guid user
        ? user : throw new PublicInsightReadException(401, "UNAUTHORIZED", "Gateway user context required");

    public static WebApplication UsePublicInsightReads(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!IsPublicInsightRead(context.Request.Path))
            { await next(context); return; }
            context.Response.Headers.CacheControl = "private, no-store";
            var headers = context.Request.Headers["X-User-Id"];
            if (headers.Count != 1 || headers[0] is not { Length: 36 } raw || !Guid.TryParseExact(raw, "D", out var user) || user == Guid.Empty)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Gateway user context required" });
                return;
            }
            context.Items[Identity] = user;
            try
            { await next(context); }
            catch (PublicInsightReadException error)
            {
                context.Response.StatusCode = error.StatusCode;
                await context.Response.WriteAsJsonAsync(new { code = error.Code, message = error.Message });
            }
            catch (BadHttpRequestException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { code = "INVALID_CURSOR", message = "Invalid read cursor or filters." });
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new { code = "INSIGHT_READ_UNAVAILABLE", message = "Insight reading is temporarily unavailable." });
            }
        });
        return app;
    }
}
