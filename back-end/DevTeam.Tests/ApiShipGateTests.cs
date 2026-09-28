using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates.Readiness;
using DevTeam.Broker.Workflow;

namespace DevTeam.Tests;

/// <summary>
/// The ship gate at the API boundary: a release can't be finalized, and a protected branch
/// can't be merged into, unless the strict checks passed — see ShipReadinessGate.
/// </summary>
public class ApiShipGateTests : IClassFixture<ApiIntegrationTests.AppFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly ApiIntegrationTests.AppFactory _factory;

    public ApiShipGateTests(ApiIntegrationTests.AppFactory factory) => _factory = factory;

    [Fact]
    public async Task Finalize_WhenTheChecksFail_IsBlockedAndDoesNotShip()
    {
        _factory.ReadinessChecker.Passed = false;
        var client = _factory.CreateClient();
        var release = await CreateReleaseAsync(client, "ship-blocked");

        var response = await client.PostAsync($"/api/releases/{release.Id}/finalize", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("didn't all pass", body);

        var after = await client.GetFromJsonAsync<DevTeamRelease>($"/api/releases/{release.Id}", JsonOptions);
        Assert.NotEqual(ReleaseStatus.Released, after!.Status);
    }

    [Fact]
    public async Task Finalize_WhenTheChecksPass_ReachesTheEnginesOwnGuard()
    {
        _factory.ReadinessChecker.Passed = true;
        var client = _factory.CreateClient();
        var release = await CreateReleaseAsync(client, "ship-passing");

        var response = await client.PostAsync($"/api/releases/{release.Id}/finalize", null);

        // The gate let it through; the engine then refuses because the feature isn't complete.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GitMerge_IntoMain_WithoutAPassingAttestation_IsBlocked()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/git/merge", new
        {
            workspacePath = @"C:\work\api-test-merge",
            sourceBranch = "release/x",
            targetBranch = "main",
        }, JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("protected", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GitMerge_IntoDevelop_IsNotBlockedByTheShipGate()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/git/merge", new
        {
            workspacePath = @"C:\work\api-test-merge",
            sourceBranch = "release/x",
            targetBranch = "develop",
        }, JsonOptions);

        // The git layer itself may fail on a missing repo, but never with the gate's 409.
        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_PostThenGet_RoundTrips()
    {
        _factory.ReadinessChecker.Passed = true;
        var client = _factory.CreateClient();
        var release = await CreateReleaseAsync(client, "readiness-round-trip");

        var posted = await client.PostAsync($"/api/releases/{release.Id}/readiness", null);
        posted.EnsureSuccessStatusCode();
        var report = await posted.Content.ReadFromJsonAsync<ReadinessReportView>(JsonOptions);
        Assert.True(report!.Passed);

        var latest = await client.GetFromJsonAsync<ReadinessReportView>($"/api/releases/{release.Id}/readiness", JsonOptions);
        Assert.Equal(report.Id, latest!.Id);
        Assert.Single(latest.Checks);
    }

    [Fact]
    public async Task Checks_DescribeTheProjectsOwnChecksInPlainLanguage()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(workspace, "front-end"));
        File.WriteAllText(Path.Combine(workspace, "front-end", "package.json"), "{}");

        try
        {
            var client = _factory.CreateClient();
            var checks = await client.GetFromJsonAsync<CheckDefinition[]>(
                $"/api/checks?workspacePath={Uri.EscapeDataString(workspace)}", JsonOptions);

            Assert.NotNull(checks);
            Assert.Contains(checks!, c => c.Id == "backend-unit" && c.Title == "The backend tests pass");
            var lint = checks!.Single(c => c.Id == "frontend-lint");
            Assert.Equal("frontend", lint.Category);
            Assert.False(string.IsNullOrWhiteSpace(lint.WhyItMatters));
            Assert.False(string.IsNullOrWhiteSpace(lint.HowToFix));
            // The raw command is carried only for the technical-details disclosure.
            Assert.Contains("next lint", lint.Technical);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private static async Task<DevTeamRelease> CreateReleaseAsync(HttpClient client, string key)
    {
        var response = await client.PostAsJsonAsync("/api/releases", new
        {
            releaseKey = key,
            workspacePath = @"C:\work\api-test-ship",
        }, JsonOptions);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<DevTeamRelease>(JsonOptions))!;
    }
}
