using System.Text.Json;
using DevTeam.Broker;
using DevTeam.Broker.Rpc;
using Microsoft.AspNetCore.Http;

namespace DevTeam.Tests;

/// <summary>
/// The single place where a thrown exception becomes an HTTP status. Every endpoint gets these
/// mappings, whether or not it has a try/catch of its own.
///
/// The point of the class is the invariant, not the individual codes: before it existed, each
/// endpoint decided for itself which exceptions it knew about. That is why the same agent launch
/// failure was a 500 on <c>/api/releases</c> (no catch), a 400 on <c>/api/features/*/start-stage</c>
/// (an unrelated catch won the race) and a 502 on the routes that happened to sit behind
/// <see cref="AgentErrorMiddleware"/>. A bug fix then had to be applied 90 times to be complete.
///
/// So these tests pin the *taxonomy* — one row per failure class, one shared answer — rather than
/// re-asserting a status code at each of the 90 call sites.
/// </summary>
public sealed class ApiErrorMapperTests
{
    [Theory]
    // A missing row (feature, release, hotfix, session) is a 404 with no body: the route's own
    // contract, and what every endpoint already did by hand.
    [InlineData(typeof(KeyNotFoundException), StatusCodes.Status404NotFound)]
    // A rejected request — a stage that cannot be pushed back from, a locked pipeline slot — is the
    // caller's fault, and the exception message is the explanation.
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status400BadRequest)]
    public void KnownFailureClasses_MapToTheirStatus(Type exceptionType, int expectedStatus)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        var mapped = ApiErrorMapper.Map(exception);

        Assert.NotNull(mapped);
        Assert.Equal(expectedStatus, mapped.Value.StatusCode);
    }

    [Fact]
    public void InvalidOperation_ExplainsItselfInTheResponseBody()
    {
        var mapped = ApiErrorMapper.Map(new InvalidOperationException("The stage is still running."));

        Assert.Equal("The stage is still running.", mapped!.Value.Message);
    }

    [Fact]
    public void NotFound_HasNoBody()
    {
        var mapped = ApiErrorMapper.Map(new KeyNotFoundException("no such feature"));

        Assert.Equal(string.Empty, mapped!.Value.Message);
    }

    [Fact]
    public void ACancelledTurn_IsAConflict_AndSaysSoInPlainWords()
    {
        var mapped = ApiErrorMapper.Map(new OperationCanceledException());

        Assert.Equal(StatusCodes.Status409Conflict, mapped!.Value.StatusCode);
        Assert.Contains("cancel", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AClientThatWentAway_IsNotOurError_SoItIsNotTranslated()
    {
        // The caller hung up; there is nobody left to read a status. Translating it would invent a
        // failure that did not happen.
        var mapped = ApiErrorMapper.Map(new OperationCanceledException(), clientCancelled: true);

        Assert.Null(mapped);
    }

    [Fact]
    public void AProviderRefusal_IsABadGateway_NotABrokerBug()
    {
        var mapped = ApiErrorMapper.Map(new RpcException("invalid_request_error", -1));

        Assert.Equal(StatusCodes.Status502BadGateway, mapped!.Value.StatusCode);
        Assert.Contains("try again", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFreeTierRefusal_ExplainsItselfAndSuggestsWhatToDo()
    {
        // The provider's own wording, as seen in the logs.
        var mapped = ApiErrorMapper.Map(new RpcException(
            "Internal error: Error from provider (Console): OpenCode's free tier can only be used from within OpenCode",
            -32603));

        Assert.Equal(StatusCodes.Status502BadGateway, mapped!.Value.StatusCode);
        Assert.Contains("free", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("temporary", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("different model", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAgentThatDisconnected_IsABadGateway_AndSaysItWillRestartFresh()
    {
        var mapped = ApiErrorMapper.Map(new AcpDisconnectedException("pipe closed"));

        Assert.Equal(StatusCodes.Status502BadGateway, mapped!.Value.StatusCode);
        Assert.Contains("stopped", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fresh session", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAgentThatCannotBeLaunched_IsUnavailable_NotAnInternalError()
    {
        // The one that bit in the wild: `start-stage` answered a bare 500 because nothing anywhere
        // knew what a failed process launch meant. The CLI is a dependency, and a dependency that
        // is not runnable is 503 — retryable, and honest about what the user can act on.
        var mapped = ApiErrorMapper.Map(
            new AgentLaunchException(
                "Windows refused to start the agent: error 448, untrusted mount point.",
                new System.ComponentModel.Win32Exception(448)));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, mapped!.Value.StatusCode);
        Assert.Contains("opencode", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winget install opencode", mapped.Value.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnAgentLaunchFailure_DoesNotLeakAStackTraceOrAnExceptionTypeName()
    {
        var mapped = ApiErrorMapper.Map(
            new AgentLaunchException("nope", new System.ComponentModel.Win32Exception(448)));

        Assert.DoesNotContain("   at ", mapped!.Value.Message);
        Assert.DoesNotContain(nameof(AgentLaunchException), mapped.Value.Message);
        Assert.DoesNotContain("Win32Exception", mapped.Value.Message);
    }

    [Fact]
    public void AFailedLaunch_StillNamesTheUnderlyingWindowsError_SoSupportCanAct()
    {
        // The user-facing text is generic on purpose; the *reason* is what makes it fixable, and it
        // is not guessable from "503" alone.
        var mapped = ApiErrorMapper.Map(
            new AgentLaunchException(
                "start failed (error 448: the path cannot be traversed because it contains an untrusted mount point)",
                new System.ComponentModel.Win32Exception(448)));

        Assert.Contains("448", mapped!.Value.Message);
    }

    [Theory]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(JsonException))]
    public void GenuinelyUnexpectedExceptions_AreNotSwallowed(Type exceptionType)
    {
        // Anything outside the taxonomy is a bug in the broker, not a client-visible condition.
        // Catching it and dressing it up as a 400 or a 503 would hide it, so it keeps propagating
        // and keeps producing a 500.
        var exception = (Exception)Activator.CreateInstance(exceptionType, "boom")!;

        Assert.Null(ApiErrorMapper.Map(exception));
    }
}
