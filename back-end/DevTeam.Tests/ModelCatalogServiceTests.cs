using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Xunit;

namespace DevTeam.Tests;

public class ModelCatalogServiceTests
{
    [Fact]
    public async Task GetAvailableModelsAsync_ReturnsModelsFromCoordinator()
    {
        var coordinator = new FakeBrokerCoordinator();
        coordinator.ModelsToReturn = [new ModelOption("claude-sonnet-4-5", "Claude Sonnet 4.5", "Anthropic")];
        var catalog = new ModelCatalogService(coordinator);

        var models = await catalog.GetAvailableModelsAsync(@"C:\work\proj", CancellationToken.None);

        Assert.Single(models);
        Assert.Equal("claude-sonnet-4-5", models[0].Value);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CachesAcrossCalls_OnlyProbesSessionOnce()
    {
        var coordinator = new FakeBrokerCoordinator();
        coordinator.ModelsToReturn = [new ModelOption("opencode/big-pickle", "OpenCode", null)];
        var catalog = new ModelCatalogService(coordinator);

        await catalog.GetAvailableModelsAsync(@"C:\work\proj", CancellationToken.None);
        await catalog.GetAvailableModelsAsync(@"C:\work\other", CancellationToken.None);

        Assert.Single(coordinator.Commands, c => c.StartsWith("new-session:"));
    }
}
