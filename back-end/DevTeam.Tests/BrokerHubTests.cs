using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DevTeam.Tests;

/// <summary>Verifies the hub contract end-to-end: join session group, receive StreamEvents while a prompt streams.</summary>
public class BrokerHubTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ApiIntegrationTests.AppFactory _factory;

    public BrokerHubTests(ApiIntegrationTests.AppFactory factory) => _factory = factory;

    // The session create/prompt routes this test used to exercise directly were Chat-tab
    // exclusive and have been removed, but the session-scoped event broadcast they exercise
    // (JoinSession/OnEvent) is not: WorkflowEngine's start-stage/send-message calls go through
    // the same PromptWithSessionRecoveryAsync that fires these events, so this now drives that
    // through the release pipeline instead.
    [Fact]
    public async Task Hub_StreamsPromptEventsToJoinedClient()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/releases",
            new { releaseKey = "feat-hub-test", workspacePath = @"C:\work\hub-test" });
        create.EnsureSuccessStatusCode();
        var release = await create.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions);
        Assert.NotNull(release);

        var startStage = await client.PostAsJsonAsync($"/api/features/{release!.CurrentFeatureId}/start-stage", new { });
        startStage.EnsureSuccessStatusCode();
        var stageRun = await startStage.Content.ReadFromJsonAsync<ReleaseStageRun>(JsonOptions);
        Assert.NotNull(stageRun);
        var sessionId = Guid.Parse(stageRun!.AcpSessionId!);

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub", opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        var received = new List<StreamEvent>();
        connection.On("OnEvent", (StreamEvent evt) => received.Add(evt));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinSession", sessionId.ToString());

        var prompt = await client.PostAsJsonAsync($"/api/features/{release.CurrentFeatureId}/send-message",
            new { text = "hi from hub" });
        prompt.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (received.All(e => e.Type != BrokerCoordinator.EventTurnEnd) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains(received, e => e.Type == BrokerCoordinator.EventTextDelta
            && e.SessionId == sessionId.ToString());
        Assert.Contains(received, e => e.Type == BrokerCoordinator.EventTurnEnd);

        await connection.DisposeAsync();
    }
}