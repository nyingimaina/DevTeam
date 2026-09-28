using DevTeam.Broker.Rpc;
using DevTeam.Broker.Workflow;

namespace DevTeam.Broker;

/// <summary>An HTTP status and the plain-language text to send with it.</summary>
public readonly record struct ApiError(int StatusCode, string Message);

/// <summary>
/// The one place where a thrown exception becomes an HTTP status.
///
/// <para>
/// This exists because the mapping used to be scattered. Every endpoint that had a try/catch
/// decided for itself which exceptions it knew about, so the same failure answered differently
/// depending on the route: an agent that would not start was a 500 on <c>/api/releases</c>, a 400
/// on <c>/api/features/*/start-stage</c> (where an unrelated <c>InvalidOperationException</c> catch
/// won the race) and a 502 behind <see cref="AgentErrorMiddleware"/>. Fixing a failure class then
/// meant editing up to ninety handlers, which is why nobody fixed it — the 500 in the wild sat
/// there through two releases.
/// </para>
///
/// <para>
/// So the contract is deliberately narrow and deliberate. Exceptions in the taxonomy below get one
/// shared answer, on every route, with no per-endpoint code. Anything outside the taxonomy returns
/// <c>null</c> and keeps propagating: those are broker bugs, and dressing them up as a 400 or a 503
/// would hide them behind a helpful-looking message.
/// </para>
/// </summary>
public static class ApiErrorMapper
{
    /// <summary>Plain-language text for a deliberate cancellation — safe to show a person.</summary>
    public const string CancelledMessage = "The run was cancelled.";

    public static ApiError? Map(Exception ex, bool clientCancelled = false) => ex switch
    {
        // The caller hung up; nobody is left to read a status, and reporting a failure that did not
        // happen would be worse than staying quiet.
        OperationCanceledException when clientCancelled => null,
        OperationCanceledException =>
            new ApiError(StatusCodes.Status409Conflict, CancelledMessage),

        // A missing row (feature, release, hotfix, session) is a 404 with no body, which is the
        // contract the routes already had.
        KeyNotFoundException =>
            new ApiError(StatusCodes.Status404NotFound, string.Empty),

        // A rejected request — a stage that cannot be pushed back from, a path that escapes the
        // workspace. The exception message is the explanation.
        InvalidOperationException invalid =>
            new ApiError(StatusCodes.Status400BadRequest, invalid.Message),
        ArgumentException argument =>
            new ApiError(StatusCodes.Status400BadRequest, argument.Message),
        WorkflowConfigurationException configuration =>
            new ApiError(StatusCodes.Status400BadRequest, configuration.Message),

        // The CLI is a dependency of the agent, and a dependency that cannot be run is unavailable
        // rather than broken. 503 also tells the client this is worth retrying.
        AgentLaunchException launch =>
            new ApiError(StatusCodes.Status503ServiceUnavailable, DescribeLaunchFailure(launch)),

        RpcException rpc when IsFreeTierRefusal(rpc.Message) =>
            new ApiError(StatusCodes.Status502BadGateway, FreeTierMessage),
        RpcException rpc =>
            new ApiError(StatusCodes.Status502BadGateway, ProviderMessage(rpc.Message)),

        AcpDisconnectedException =>
            new ApiError(
                StatusCodes.Status502BadGateway,
                "The AI agent stopped unexpectedly. Try again — it will start a fresh session."),

        _ => null,
    };

    private const string FreeTierMessage =
        "The AI service refused this request (it said its free model can only be used from within OpenCode). " +
        "This is often temporary, so try again. If it keeps happening, pick a different model in settings.";

    private static string ProviderMessage(string providerMessage) =>
        $"The AI service refused this request: {providerMessage}. Check the model settings and try again.";

    /// <summary>
    /// What the user is told when the agent process cannot be started, and why it is shaped this way:
    /// the fix is on their machine, not in the app, so the message has to name the tool and the
    /// commands. The underlying Windows reason is included because "503" alone is not actionable and
    /// a support request is the usual next step.
    /// </summary>
    private static string DescribeLaunchFailure(AgentLaunchException launch) =>
        "The opencode command-line tool is installed but Windows refused to start it, so the AI agent " +
        "cannot run right now. " +
        $"({Single(launch.Message)}) " +
        "Reinstall it with \"winget install opencode\" or \"npm install -g opencode-ai\", " +
        "or set OPENCODE_PATH to the opencode executable.";

    // A message can carry more than a line if something upstream included an exception's ToString().
    // Stack frames and type names are noise here, and the response is what the user reads.
    private static string Single(string message)
    {
        var firstLine = message.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
            firstLine = firstLine[..newline];
        return firstLine.Length > 400 ? string.Concat(firstLine[..400], "…") : firstLine.ToString();
    }

    private static bool IsFreeTierRefusal(string message) =>
        message.Contains("free tier", StringComparison.OrdinalIgnoreCase);
}
