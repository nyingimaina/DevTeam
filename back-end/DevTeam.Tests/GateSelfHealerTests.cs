using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class GateSelfHealerTests : IDisposable
{
    private const string GherkinEvidence =
        "fail: REQ-004: acceptance criteria missing Given\r\nok:   REQ-001\r\nok:   REQ-002";

    private readonly SqliteConnection _connection;
    private readonly FakeGateRunner _gateRunner = new();
    private readonly FakeBrokerCoordinator _coordinator = new();

    public GateSelfHealerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static GateResult Pass() => new(true, "OK", "");
    private static GateResult Fail(string evidence) => new(false, "failed", evidence);

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new Factory(_connection);

    private sealed class Factory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, new RecordingBroadcaster(),
        new FakeGitService(), new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), new FakeGitCredentialStore(), new ActiveTurnTracker());

    // Leading builtins (scaffold, core scaffold, repo hygiene, code map, context) consume five
    // results at start-stage, then each exit pass consumes two (gherkin_validator, render_handoff)
    // for the default BA role.
    private void ScriptGherkinResults(params bool[] gherkinPassesPerRun)
    {
        _gateRunner.Results.Add(Pass());
        _gateRunner.Results.Add(Pass());
        _gateRunner.Results.Add(Pass());
        _gateRunner.Results.Add(Pass());
        _gateRunner.Results.Add(Pass());
        foreach (var passes in gherkinPassesPerRun)
        {
            _gateRunner.Results.Add(passes ? Pass() : Fail(GherkinEvidence));
            _gateRunner.Results.Add(Pass());
        }
    }

    private async Task<(WorkflowEngine Engine, Guid FeatureId)> DriveToChatAsync()
    {
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);
        await engine.SendMessageAsync(featureId, "We need a login form", CancellationToken.None);
        return (engine, featureId);
    }

    [Fact]
    public async Task GatesPassFirstTime_NoAutoFixAndPassedOutcome()
    {
        ScriptGherkinResults(true);
        var (engine, featureId) = await DriveToChatAsync();

        var result = await new GateSelfHealer(engine).RunAsync(featureId, CancellationToken.None);

        Assert.Equal(GateHealOutcome.Passed, result.Outcome);
        Assert.Equal(0, result.AutoFixAttempts);
        Assert.Empty(result.Problems);
        Assert.DoesNotContain(_coordinator.Prompts, p => p.Contains("automatic checks on your work"));
    }

    [Fact]
    public async Task GateFailsOnce_AgentIsAskedToFixItAndSecondRunPasses()
    {
        ScriptGherkinResults(false, true);
        var (engine, featureId) = await DriveToChatAsync();

        var result = await new GateSelfHealer(engine).RunAsync(featureId, CancellationToken.None);

        Assert.Equal(GateHealOutcome.Passed, result.Outcome);
        Assert.Equal(1, result.AutoFixAttempts);
        Assert.Empty(result.Problems);
        var fixPrompt = Assert.Single(_coordinator.Prompts, p => p.Contains("REQ-004"));
        Assert.Contains("DONE", fixPrompt);
    }

    [Fact]
    public async Task GateKeepsFailing_StopsAtTheCapAndHandsBackToTheUser()
    {
        ScriptGherkinResults(false, false, false, false);
        var (engine, featureId) = await DriveToChatAsync();

        var result = await new GateSelfHealer(engine, maxAutoFixes: 2)
            .RunAsync(featureId, CancellationToken.None);

        Assert.Equal(GateHealOutcome.NeedsYou, result.Outcome);
        Assert.Equal(2, result.AutoFixAttempts);
        Assert.Equal(2, _coordinator.Prompts.Count(p => p.Contains("REQ-004")));
        var problem = Assert.Single(result.Problems);
        Assert.Equal("gherkin_validator", problem.GateName);
        Assert.DoesNotContain("gherkin", problem.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gherkin", problem.WhatWentWrong, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ReleaseStageStatus.BlockedGate,
            result.Release.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);
    }

    [Fact]
    public async Task ZeroAutoFixes_BehavesLikeAPlainRunGates()
    {
        ScriptGherkinResults(false);
        var (engine, featureId) = await DriveToChatAsync();
        var promptsBefore = _coordinator.Prompts.Count;

        var result = await new GateSelfHealer(engine, maxAutoFixes: 0)
            .RunAsync(featureId, CancellationToken.None);

        Assert.Equal(GateHealOutcome.NeedsYou, result.Outcome);
        Assert.Equal(0, result.AutoFixAttempts);
        Assert.Equal(promptsBefore, _coordinator.Prompts.Count);
    }

    [Fact]
    public async Task ReopenBlockedGate_TurnsBlockedGateBackIntoActive()
    {
        ScriptGherkinResults(false);
        var (engine, featureId) = await DriveToChatAsync();
        await engine.RunGatesAsync(featureId, CancellationToken.None);

        var release = await engine.ReopenBlockedGateAsync(featureId, CancellationToken.None);

        Assert.Equal(ReleaseStageStatus.Active,
            release.StageRuns.Single(sr => sr.StageName == "business-analyst").Status);
    }

    [Fact]
    public async Task ReopenBlockedGate_WhenNothingIsBlocked_Throws()
    {
        var (engine, featureId) = await DriveToChatAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.ReopenBlockedGateAsync(featureId, CancellationToken.None));
    }
}

public class GateFriendlyTextTests
{
    [Fact]
    public void RequirementFormatCheck_IsExplainedInPlainLanguage()
    {
        var problem = GateFriendlyText.Describe(
            BuiltinRegistry.GherkinValidator,
            "fail: REQ-004: acceptance criteria missing Given\r\nok:   REQ-001");

        Assert.DoesNotContain("gherkin", problem.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Requirement 4", problem.WhatWentWrong);
        Assert.DoesNotContain("REQ-004", problem.WhatWentWrong);
        Assert.DoesNotContain("acceptance criteria", problem.WhatWentWrong, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("starting situation", problem.WhatWentWrong);
        Assert.Contains("REQ-004", problem.TechnicalDetail);
    }

    [Theory]
    [InlineData("When")]
    [InlineData("Then")]
    public void MissingWhenOrThen_IsNamedInPlainLanguage(string keyword)
    {
        var problem = GateFriendlyText.Describe(
            BuiltinRegistry.GherkinValidator, $"fail: REQ-002: acceptance criteria missing {keyword}");

        Assert.Contains("Requirement 2", problem.WhatWentWrong);
        Assert.DoesNotContain(keyword, problem.WhatWentWrong);
    }

    [Theory]
    [InlineData(BuiltinRegistry.ScaffoldSpecs)]
    [InlineData(BuiltinRegistry.CoreScaffold)]
    [InlineData(BuiltinRegistry.RepoHygiene)]
    [InlineData(BuiltinRegistry.ContextBundle)]
    [InlineData(BuiltinRegistry.CodeMap)]
    [InlineData(BuiltinRegistry.GherkinValidator)]
    [InlineData(BuiltinRegistry.VerifyCode)]
    [InlineData(BuiltinRegistry.CodeHygiene)]
    [InlineData(BuiltinRegistry.AppLaunch)]
    [InlineData(BuiltinRegistry.SliceGuard)]
    [InlineData(BuiltinRegistry.SliceScope)]
    [InlineData(BuiltinRegistry.ReuseGate)]
    [InlineData(BuiltinRegistry.ProjectStructure)]
    [InlineData(BuiltinRegistry.RenderPr)]
    [InlineData(BuiltinRegistry.RenderHandoff)]
    [InlineData(BuiltinRegistry.CoverageMatrix)]
    public void EveryKnownCheck_HasAJargonFreeTitle(string gateName)
    {
        var problem = GateFriendlyText.Describe(gateName, "some evidence");

        Assert.NotEqual(gateName, problem.Title);
        Assert.DoesNotContain("_", problem.Title);
        Assert.False(string.IsNullOrWhiteSpace(problem.WhatWentWrong));
        Assert.DoesNotContain("gherkin", problem.Title + problem.WhatWentWrong, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GateCheckRow_ExposesPlainLanguageTitleAndProblemForTheUi()
    {
        var check = new ReleaseGateCheck
        {
            Name = BuiltinRegistry.GherkinValidator,
            Passed = false,
            EvidenceText = "fail: REQ-004: acceptance criteria missing Given",
        };

        Assert.DoesNotContain("gherkin", check.DisplayTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Requirement 4", check.PlainProblem);
    }

    [Fact]
    public void UnknownCheck_FallsBackToAReadableTitleAndKeepsTheEvidence()
    {
        var problem = GateFriendlyText.Describe("my_custom_check", "boom");

        Assert.Equal("My custom check", problem.Title);
        Assert.Contains("boom", problem.TechnicalDetail);
    }
}
