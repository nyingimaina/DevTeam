namespace DevTeam.Broker;

/// <summary>
/// Adds always-on diagnostics to every /api/* response: how long the request took on
/// the server, and what Content-Length the client declared. Visible directly via
/// `curl -i` when investigating a slow or hanging request, without needing server logs —
/// e.g. a client that declares a Content-Length but sends a mismatched or absent body
/// (a common cause of an apparent "hang" that has nothing to do with the app itself)
/// shows up immediately as a declared length with no matching request completing.
/// </summary>
public static class RequestDiagnosticsMiddleware
{
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
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Request-Elapsed-Ms"] = stopwatch.ElapsedMilliseconds.ToString();
                context.Response.Headers["X-Request-Declared-Length"] = declaredLength;
                return Task.CompletedTask;
            });

            var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogInformation(
                "API {Method} {Path} started; declared Content-Length={DeclaredLength}",
                context.Request.Method, context.Request.Path, declaredLength);

            await next();

            stopwatch.Stop();
            logger.LogInformation(
                "API {Method} {Path} finished status={StatusCode} elapsedMs={ElapsedMs}",
                context.Request.Method, context.Request.Path, context.Response.StatusCode, stopwatch.ElapsedMilliseconds);
        });
    }
}
