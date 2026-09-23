using DevTeam.Broker;
using DevTeam.Broker.Rpc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DevTeam.Tests;

public class AgentErrorMiddlewareTests
{
    private const string FreeTierMessage =
        "Internal error: Error from provider (Console): OpenCode's free tier can only be used from within OpenCode";

    private static async Task<(DefaultHttpContext Ctx, string Body)> InvokeAsync(RequestDelegate next, string path = "/api/features/x/run-stage")
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();

        await AgentErrorMiddleware.InvokeAsync(ctx, next);

        ctx.Response.Body.Position = 0;
        return (ctx, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact]
    public async Task ProviderRejection_BecomesA502WithAPlainMessage_InsteadOfEscaping()
    {
        var (ctx, body) = await InvokeAsync(_ => throw new RpcException(FreeTierMessage, -32603));

        Assert.Equal(StatusCodes.Status502BadGateway, ctx.Response.StatusCode);
        Assert.Contains("refused", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RpcException", body);
        Assert.DoesNotContain("   at ", body); // no stack trace
    }

    [Fact]
    public async Task FreeTierRejection_TellsTheUserWhatToDo()
    {
        var (_, body) = await InvokeAsync(_ => throw new RpcException(FreeTierMessage, -32603));

        Assert.Contains("free", body, StringComparison.OrdinalIgnoreCase);
        // Logs show this refusal is intermittent (1 of ~12,000 free-model requests) — so it must
        // read as "try again" first, never as "this model can't be used".
        Assert.Contains("temporary", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("try again", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("different model", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("can't be used", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OtherProviderRejection_StillGetsAGenericButUsefulMessage()
    {
        var (ctx, body) = await InvokeAsync(_ => throw new RpcException("Upstream request failed: invalid_request_error", -32603));

        Assert.Equal(StatusCodes.Status502BadGateway, ctx.Response.StatusCode);
        Assert.Contains("invalid_request_error", body); // keep the provider's own words for support
        Assert.Contains("try again", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgentDisconnect_BecomesA502()
    {
        var (ctx, body) = await InvokeAsync(_ => throw new AcpDisconnectedException("pipe closed"));

        Assert.Equal(StatusCodes.Status502BadGateway, ctx.Response.StatusCode);
        Assert.Contains("stopped", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnrelatedExceptions_AreLeftAlone()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokeAsync(_ => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public async Task CancelledTurn_IsHandledGracefully_NotEscapedAsAnUnhandledException()
    {
        var (ctx, body) = await InvokeAsync(_ => throw new OperationCanceledException("A task was canceled."));

        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
        Assert.Contains("cancel", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", body); // no stack trace
        Assert.DoesNotContain("TaskCanceledException", body);
    }

    [Fact]
    public async Task TaskCanceledException_IsAlsoHandledGracefully()
    {
        var (ctx, _) = await InvokeAsync(_ => throw new TaskCanceledException());

        Assert.Equal(StatusCodes.Status409Conflict, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task RequestsThatSucceed_PassThroughUntouched()
    {
        var (ctx, body) = await InvokeAsync(async c => { c.Response.StatusCode = 200; await c.Response.WriteAsync("ok"); });

        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal("ok", body);
    }

    [Fact]
    public async Task IfTheResponseAlreadyStarted_TheOriginalExceptionIsRethrown()
    {
        // Can't change the status once bytes have gone out — swallowing it would hide the failure.
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await ctx.Response.WriteAsync("partial");
        await ctx.Response.Body.FlushAsync();
        ctx.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>(new StartedResponseFeature(ctx.Response.Body));

        await Assert.ThrowsAsync<RpcException>(
            () => AgentErrorMiddleware.InvokeAsync(ctx, _ => throw new RpcException(FreeTierMessage, -32603)));
    }

    private sealed class StartedResponseFeature(Stream body) : Microsoft.AspNetCore.Http.Features.IHttpResponseFeature
    {
        public int StatusCode { get; set; } = 200;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
}
