using System.Net.Http.Json;
using DevTeam.Broker.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DevTeam.Tests;

/// <summary>Verifies the hub contract end-to-end: join session group, receive StreamEvents while a prompt streams.</summary>
public class BrokerHubTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private readonly ApiIntegrationTests.AppFactory _factory;

    public BrokerHubTests(ApiIntegrationTests.AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Hub_StreamsPromptEventsToJoinedClient()
    {
        var client = _factory.CreateClient();
        var create = await client.PostAsJsonAsync("/api/sessions", new { workspacePath = @"C:\work\hub-test" });
        create.EnsureSuccessStatusCode();
        var session = await create.Content.ReadFromJsonAsync<SessionSummary>();
        Assert.NotNull(session);

        var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hub", opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        var received = new List<StreamEvent>();
        connection.On("OnEvent", (StreamEvent evt) => received.Add(evt));

        await connection.StartAsync();
        await connection.InvokeAsync("JoinSession", session!.SessionId.ToString());

        var prompt = await client.PostAsJsonAsync($"/api/sessions/{session.SessionId}/prompt",
            new { text = "hi from hub" });
        prompt.EnsureSuccessStatusCode();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (received.All(e => e.Type != BrokerCoordinator.EventTurnEnd) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.Contains(received, e => e.Type == BrokerCoordinator.EventTextDelta
            && e.SessionId == session.SessionId.ToString());
        Assert.Contains(received, e => e.Type == BrokerCoordinator.EventTurnEnd);

        await connection.DisposeAsync();
    }
}