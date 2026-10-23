using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace InsightService.Middleware;

public static partial class RequestMiddleware
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Insight request failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception);

    public static void UseInsightRequestHandling(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Path.StartsWithSegments("/insight-service/internal"))
                {
                    var expected = app.Configuration["Internal:Secret"];
                    var supplied = context.Request.Headers["X-Internal-Secret"].ToString();
                    if (string.IsNullOrEmpty(expected) ||
                        string.IsNullOrEmpty(supplied) ||
                        !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                            SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                    {
                        context.Response.StatusCode = 403;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            code = "internal_access_denied",
                            message = "A configured internal credential is required."
                        });
                        return;
                    }
                }
                await next(context);
            }
            catch (BadHttpRequestException)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "invalid_request",
                    message = "The request body or parameters are invalid."
                });
            }
            catch (PostgresException e) when (e.SqlState is "55P03" or "40P01" or "40001" or "57014")
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "storage_busy",
                    message = "Local storage is busy; no successful result is implied."
                });
            }
            catch (Exception e)
            {
                LogRequestFailed(app.Logger, e);
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "internal_error",
                    message = "The request failed; no successful result is implied."
                });
            }
        });
    }
}
