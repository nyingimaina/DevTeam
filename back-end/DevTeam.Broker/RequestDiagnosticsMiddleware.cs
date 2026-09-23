using Serilog.Context;

namespace DevTeam.Broker;

/// <summary>
/// Adds always-on diagnostics to every /api/* response: a short, quotable request id, how long
/// the request took on the server, and what Content-Length the client declared. The id is echoed
/// back in a header (and into the response body of a failure) so a user can say "it failed,
/// reference abc123" and a specialist can find exactly that request in the logs — without either
/// of them having to read a stack trace.
/// </summary>
public static class RequestDiagnosticsMiddleware
{
    public const string RequestIdHeader = "X-Request-Id";

    public static void UseRequestDiagnostics(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next();
                return;
            }

            var declaredLength = context.Request.ContentLength?.ToString() ?? "none";

            // Honour an id supplied by the client so a retry stays tied to the same incident.
            var requestId = context.Request.Headers.TryGetValue(RequestIdHeader, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
                ? incoming.ToString()
                : Guid.NewGuid().ToString("N")[..12];

            context.Response.Headers[RequestIdHeader] = requestId;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Request-Elapsed-Ms"] = stopwatch.ElapsedMilliseconds.ToString();
                context.Response.Headers["X-Request-Declared-Length"] = declaredLength;
                return Task.CompletedTask;
            });

            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            using (LogContext.PushProperty("RequestId", requestId))
            {
                logger.LogInformation(
                    "API {Method} {Path} started; declared Content-Length={DeclaredLength}",
                    context.Request.Method, context.Request.Path, declaredLength);

                await next();

                stopwatch.Stop();
                logger.LogInformation(
                    "API {Method} {Path} finished status={StatusCode} elapsedMs={ElapsedMs}",
                    context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
            }
        });
    }
}
