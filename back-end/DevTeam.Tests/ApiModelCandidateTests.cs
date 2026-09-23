using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker.Models;
using DevTeam.Broker.Server;

namespace DevTeam.Tests;

/// <summary>
/// The model failover list, as the Settings → Models screen uses it.
/// </summary>
public class ApiModelCandidateTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    // Each test gets its own workspace so they cannot interfere through the shared test database.
    private const string SeedWorkspace = @"D:\apps\api-models-seed";
    private const string MutateWorkspace = @"D:\apps\api-models-mutate";
    private const string DuplicateWorkspace = @"D:\apps\api-models-duplicate";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ApiIntegrationTests.AppFactory _factory;

    public ApiModelCandidateTests(ApiIntegrationTests.AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Candidates_SeedTheCuratedListWithTheDefaultFirst()
    {
        var client = _factory.CreateClient();

        var candidates = await GetAsync(client, SeedWorkspace);

        Assert.Equal(ModelCatalog.SeedLimit, candidates.Length);
        Assert.Equal("opencode/big-pickle", candidates[0].ModelId);
        Assert.Equal(0, candidates[0].Priority);
        // Read-only stats travel with each row so the UI can explain the order.
        Assert.NotNull(candidates[0].Cost);
        Assert.NotNull(candidates[0].Smartness);
        // The seed alternates providers so a provider-level failure has somewhere to go.
        var providers = candidates.Select(c => c.ModelId.Split('/')[0]).ToList();
        for (var i = 1; i < providers.Count; i++)
            Assert.NotEqual(providers[i - 1], providers[i]);
    }

    [Fact]
    public async Task Candidates_CanBeAddedDisabledReorderedAndRemoved()
    {
        var client = _factory.CreateClient();
        var seeded = await GetAsync(client, MutateWorkspace);

        // Add: a model we have no estimate for still works, and simply reports "unknown" stats.
        var addResponse = await client.PostAsJsonAsync("/api/models/candidates",
            new { workspacePath = MutateWorkspace, modelId = "custom/my-model" }, JsonOptions);
        addResponse.EnsureSuccessStatusCode();
        var added = (await addResponse.Content.ReadFromJsonAsync<ModelCandidateDto>(JsonOptions))!;
        Assert.True(added.UserAdded);
        Assert.Null(added.Cost);
        Assert.Null(added.Smartness);

        // Disable the first entry: it must still be listed, just off.
        var first = seeded[0];
        (await client.PostAsJsonAsync($"/api/models/candidates/{first.Id}/enabled", new { enabled = false }, JsonOptions))
            .EnsureSuccessStatusCode();
        Assert.False((await GetAsync(client, MutateWorkspace)).Single(c => c.Id == first.Id).Enabled);

        // Reorder: move the last entry to the front.
        var ids = (await GetAsync(client, MutateWorkspace)).Select(c => c.Id).Reverse().ToArray();
        var reorderResponse = await client.PostAsJsonAsync("/api/models/candidates/reorder",
            new { workspacePath = MutateWorkspace, orderedIds = ids }, JsonOptions);
        reorderResponse.EnsureSuccessStatusCode();
        Assert.Equal(ids, (await reorderResponse.Content.ReadFromJsonAsync<ModelCandidateDto[]>(JsonOptions))!.Select(c => c.Id));

        // Remove.
        var remove = await client.DeleteAsync($"/api/models/candidates/{added.Id}");
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        Assert.DoesNotContain(await GetAsync(client, MutateWorkspace), c => c.Id == added.Id);
    }

    [Fact]
    public async Task Candidates_RejectAModelThatIsAlreadyListed()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/models/candidates",
            new { workspacePath = DuplicateWorkspace, modelId = "opencode/big-pickle" }, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<ModelCandidateDto[]> GetAsync(HttpClient client, string workspacePath)
        => (await client.GetFromJsonAsync<ModelCandidateDto[]>(
            $"/api/models/candidates?workspacePath={Uri.EscapeDataString(workspacePath)}", JsonOptions))!;
}
