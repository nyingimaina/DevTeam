using DevTeam.Broker.Rpc;

namespace DevTeam.Broker;

/// <summary>
/// The engine already records a failed agent turn on the stage run (Escalated +
/// LastErrorKind — see WorkflowEngine.RecordPromptFailureAsync) and then rethrows so callers can
/// react. Left uncaught, that rethrow reaches the client as a raw 500 with a stack trace and stops
/// a debugger as "unhandled". This turns it into what it really is — the AI provider or the agent
/// process failing, not a broker bug — with a message a non-technical user can act on.
/// </summary>
public static class AgentErrorMiddleware
{
    public static void UseAgentErrorHandling(this WebApplication app)
        => app.Use((context, next) => InvokeAsync(context, next));

    public static async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (Describe(ex) is not null && !context.Response.HasStarted)
        {
            var logger = context.RequestServices.GetService<ILogger<Program>>();
            logger?.LogWarning(ex, "Agent call failed for {Method} {Path}", context.Request.Method, context.Request.Path);

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(Describe(ex)!);
        }
    }

    /// <summary>Plain-language text for an agent failure, or null when the exception isn't one.</summary>
    public static string? Describe(Exception ex) => ex switch
    {
        RpcException rpc when IsFreeTierRefusal(rpc.Message) =>
            "The AI service refused this request (it said its free model can only be used from within OpenCode). " +
            "This is often temporary, so try again. If it keeps happening, pick a different model in settings.",
        RpcException rpc =>
            $"The AI service refused this request: {rpc.Message}. " +
            "Check the model settings and try again.",
        AcpDisconnectedException =>
            "The AI agent stopped unexpectedly. Try again — it will start a fresh session.",
        _ => null,
    };

    private static bool IsFreeTierRefusal(string message)
        => message.Contains("free tier", StringComparison.OrdinalIgnoreCase);
}
