using DevTeam.Broker.Rpc;

namespace DevTeam.Broker;

/// <summary>
/// Runs every request's failure through <see cref="ApiErrorMapper"/>, so one mapping serves all
/// ninety endpoints.
///
/// <para>
/// The engine already records a failed agent turn on the stage run (Escalated +
/// LastErrorKind — see WorkflowEngine.RecordPromptFailureAsync) and then rethrows so callers can
/// react. Left uncaught, that rethrow reaches the client as a raw 500 with a stack trace and stops
/// a debugger as "unhandled". This turns it into what it really is — the AI provider or the agent
/// process failing, not a broker bug — with a message a non-technical user can act on.
/// </para>
///
/// <para>
/// The taxonomy itself lives in <see cref="ApiErrorMapper"/>, not here, so that adding a failure
/// class means editing one place instead of a middleware plus every route.
/// </para>
/// </summary>
public static class AgentErrorMiddleware
{
    /// <summary>Plain-language text for a deliberate cancellation — safe to show a person.</summary>
    public const string CancelledMessage = ApiErrorMapper.CancelledMessage;

    public static void UseAgentErrorHandling(this WebApplication app)
        => app.Use((context, next) => InvokeAsync(context, next));

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var error = ApiErrorMapper.Map(ex, context.RequestAborted.IsCancellationRequested);
            if (error is null)
                throw;

            // A user-cancelled turn (or a client that went away) is not a broker failure: no
            // stack trace, and no debugger "unhandled exception" — just a plain "cancelled"
            // response so the caller can settle.
            var logger = context.RequestServices.GetService<ILogger<Program>>();
            logger?.LogWarning(ex, "{Method} {Path} failed: {Status}", context.Request.Method, context.Request.Path, error.Value.StatusCode);

            // Read the request id before clearing: Response.Clear() drops response headers too, so
            // capturing it afterwards yielded an empty reference — the one piece of information that
            // makes a failure report actionable was being thrown away on every error.
            var requestId = context.Response.Headers.TryGetValue(RequestDiagnosticsMiddleware.RequestIdHeader, out var id)
                ? id.ToString()
                : null;

            context.Response.Clear();
            context.Response.StatusCode = error.Value.StatusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            if (!string.IsNullOrWhiteSpace(requestId))
                context.Response.Headers[RequestDiagnosticsMiddleware.RequestIdHeader] = requestId;
            if (error.Value.Message.Length > 0)
                await context.Response.WriteAsync(WithReference(requestId, error.Value.Message));
        }
    }

    // A failure message carries the request id so the user can quote it ("reference 4f3a2b1c")
    // and a specialist can jump straight to that request in the logs.
    private static string WithReference(string? requestId, string message)
        => string.IsNullOrWhiteSpace(requestId) ? message : $"{message} (reference: {requestId})";
}
