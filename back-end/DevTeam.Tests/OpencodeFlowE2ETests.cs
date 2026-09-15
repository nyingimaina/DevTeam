using DevTeam.Broker.Domain;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Server;
using DevTeam.Broker.Spoke;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace DevTeam.Tests;

/// <summary>
/// End-to-end through the real broker stack (WorkflowEngine + BrokerCoordinator +
/// OpencodeAcpSpoke) against the real <c>opencode acp</c> binary. This is the
/// mandatory verification gate for any backend change that touches the agent
/// prompt path: a prompt must never die with a TaskCanceledException because an
/// HTTP/request token was cancelled mid-turn. Skipped when opencode is missing.
/// Run with: dotnet test DevTeam.Tests --filter "FullyQualifiedName~OpencodeFlow"
/// </summary>
public class OpencodeFlowE2ETests : IDisposable
{
    private static readonly string? OpenCodePath = OpenCodeLocator.ResolvePath();

    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly string _workspace;
    private readonly ITestOutputHelper _output;

    public OpencodeFlowE2ETests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Path.Combine(Path.GetTempPath(), "devteam-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch (IOException) { }
    }

    [Fact]
    [Trait("Category", "E2E")]
    public async Task StartStage_SendMessageEnforced_Gates_AllCompleteAgainstRealOpencode()
    {
        if (OpenCodePath is null)
        {
            _output.WriteLine("opencode not installed — skipping.");
            return;
        }

        _output.WriteLine("workspace: " + _workspace);
        using var spoke = new OpencodeAcpSpoke(new OpencodeAcpProcess(OpenCodePath, ["acp"]));
        await using var coordinator = new BrokerCoordinator(
            spoke,
            CreateFactory(),
            _broadcaster,
            NullLogger<BrokerCoordinator>.Instance,
            new ActiveTurnTracker());

        var engine = new WorkflowEngine(
            CreateFactory(), new FakeGateRunner(), coordinator, _broadcaster,
            new FakeGitService(), new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
            new ModelCatalogService(coordinator), new FakeGitCredentialStore());

        // Watchdog so a stalled agent turn fails loudly instead of hanging silently.
        var scenario = ScenarioAsync(spoke, engine);
        var winner = await Task.WhenAny(scenario, Task.Delay(TimeSpan.FromMinutes(10)));
        if (winner != scenario)
        {
            _output.WriteLine("E2E scenario exceeded the 10-minute watchdog — failing loudly.");
            throw new TimeoutException("OpenCode flow E2E did not complete within 10 minutes.");
        }
        await scenario;
    }

    private async Task ScenarioAsync(OpencodeAcpSpoke spoke, WorkflowEngine engine)
    {
        // Pick the fastest working model for the message turns so the E2E completes quickly.
        _output.WriteLine("discover fast model…");
        var fastModel = await DiscoverFastModelAsync(spoke, _workspace);
        _output.WriteLine(fastModel is null ? "no usable gemini model — using default." : "using " + fastModel);

        // Stage start issues the full BA prompt to the real agent (writes requirements.md).
        _output.WriteLine("start release…");
        var release = await engine.StartReleaseAsync("feat-e2e", _workspace, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        _output.WriteLine("start stage…");
        var stageRun = await engine.StartStageAsync(featureId, CancellationToken.None);
        _output.WriteLine("stage started.");

        if (fastModel is not null)
        {
            using var db = CreateFactory().CreateDbContext();
            var brokerSession = db.Sessions.Single(s => s.Id == Guid.Parse(stageRun.AcpSessionId!));
            try
            {
                await spoke.SetModelAsync(brokerSession.AcpSessionId, fastModel, CancellationToken.None);
                _output.WriteLine("model set to " + fastModel);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"set_model failed ({ex.GetType().Name}); continuing on default model.");
            }
        }

        // The enforced path is the exact one the send-message API uses in production.
        _output.WriteLine("send enforced message…");
        var result = await engine.SendMessageEnforcingSingleQuestionAsync(
            featureId, "We need a login form with email and password.", CancellationToken.None);
        _output.WriteLine("send complete.");

        Assert.Equal("end_turn", result.Response);
        Assert.True(result.TotalTokens > 0, "expected usage to be recorded by the real agent");

        using (var db = CreateFactory().CreateDbContext())
        {
            var persisted = db.ReleaseStageRuns.Single(sr => sr.Id == stageRun.Id);
            Assert.Equal(1, persisted.QuestionCount);
            var sessionId = Guid.Parse(persisted.AcpSessionId!);
            Assert.True(db.Messages.Any(m => m.SessionId == sessionId && m.Role == "assistant"),
                "expected the agent's reply to be persisted");
        }

        // Run gates against the real agent's work; cancellation/timeout fails this test.
        _output.WriteLine("run gates…");
        var gated = await engine.RunGatesAsync(featureId, CancellationToken.None);
        _output.WriteLine("gates complete.");
        using (var db = CreateFactory().CreateDbContext())
        {
            Assert.True(db.ReleaseGateChecks.Any(),
                "expected gate checks to be recorded by RunGatesAsync");
        }
        Assert.NotEqual(ReleaseStageStatus.GatesRunning, gated.StageRuns.Single(sr => sr.Id == stageRun.Id).Status);
    }

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new SqliteDbContextFactory(_connection);

    /// <summary>
    /// Finds a fast (Gemini-class) model that <em>actually works</em> with this
    /// provider. The preferred modern ids are verified first; config-advertised
    /// ids can be stale (e.g. gemini-2.5-flash is rejected by the provider), and
    /// some advertised ids hang instead of erroring, so each candidate is given a
    /// short verification turn on the probe session and the first success wins.
    /// Returns null when none work (caller falls back to the default model).
    /// </summary>
    private async Task<string?> DiscoverFastModelAsync(OpencodeAcpSpoke spoke, string workspace)
    {
        try
        {
            var probe = await spoke.NewSessionAsync(workspace, CancellationToken.None);
            var modelOption = probe.ConfigOptions.FirstOrDefault(o =>
                string.Equals(o.Id, "model", StringComparison.OrdinalIgnoreCase));
            var advertised = (modelOption?.Options ?? [])
                .Select(v => v.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Where(v => v.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            // Preferred (currently-working) ids go first; advertised ids only as
            // fallback when none of the preferred ones verify.
            var candidates = new List<string>();
            candidates.Add("google/gemini-3.6-flash");
            candidates.Add("models/gemini-3.6-flash");
            candidates.Add("gemini-3.6-flash");
            candidates.AddRange(advertised.Where(m => m.Contains("flash", StringComparison.OrdinalIgnoreCase)));
            candidates.AddRange(advertised.Where(m => !m.Contains("flash", StringComparison.OrdinalIgnoreCase)));

            foreach (var modelId in candidates.Distinct().Take(6))
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await spoke.SetModelAsync(probe.SessionId, modelId, attempt.Token);
                    var verification = await spoke.PromptAsync(
                        probe.SessionId,
                        [new AgentPromptPart("text", "Reply with exactly: OK")],
                        attempt.Token);
                    if (string.Equals(verification.StopReason, "end_turn", StringComparison.Ordinal))
                    {
                        _output.WriteLine("verified working model: " + modelId);
                        return modelId;
                    }
                }
                catch (OperationCanceledException)
                {
                    _output.WriteLine($"model '{modelId}' hung — skipping.");
                }
                catch (Exception ex)
                {
                    _output.WriteLine($"model '{modelId}' rejected: {ex.GetType().Name} {ex.Message}");
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _output.WriteLine("model discovery failed: " + ex.GetType().Name + " " + ex.Message);
            return null;
        }
    }

    private sealed class SqliteDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}