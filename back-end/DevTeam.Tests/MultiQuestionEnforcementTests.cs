using DevTeam.Broker.Domain;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Git;
using DevTeam.Broker.Server;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Context;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevTeam.Tests;

public class MultiQuestionDetectorTests
{
    [Fact]
    public void Count_EmptyOrNull_ReturnsZero()
    {
        Assert.Equal(0, MultiQuestionDetector.CountQuestions(null));
        Assert.Equal(0, MultiQuestionDetector.CountQuestions(""));
        Assert.Equal(0, MultiQuestionDetector.CountQuestions("   "));
        Assert.False(MultiQuestionDetector.ContainsMultipleQuestions(""));
    }

    [Fact]
    public void Count_SingleQuestion_ReturnsOne()
    {
        const string text = "What should the login form do?";
        Assert.Equal(1, MultiQuestionDetector.CountQuestions(text));
        Assert.False(MultiQuestionDetector.ContainsMultipleQuestions(text));
    }

    [Fact]
    public void Count_TwoQuestions_ReturnsTwo()
    {
        const string text = "What should the feature do? Also, who is the end user?";
        Assert.Equal(2, MultiQuestionDetector.CountQuestions(text));
        Assert.True(MultiQuestionDetector.ContainsMultipleQuestions(text));
    }

    [Fact]
    public void Count_EnumeratedQuestions_FlagsAsMultiple()
    {
        const string text = "1. How should 'add' be expressed? 2. What happens for invalid inputs?";
        Assert.Equal(2, MultiQuestionDetector.CountQuestions(text));
        Assert.True(MultiQuestionDetector.ContainsMultipleQuestions(text));
    }

    [Fact]
    public void Count_IgnoresToolEchoLines()
    {
        const string text = "tool: execute\ncall_d3832abc\nWhat is your question?";
        Assert.Equal(1, MultiQuestionDetector.CountQuestions(text));
        Assert.False(MultiQuestionDetector.ContainsMultipleQuestions(text));
    }

    [Fact]
    public void Count_NoQuestionMarks_ReturnsZero()
    {
        Assert.Equal(0, MultiQuestionDetector.CountQuestions("The requirements are clear. Proceeding to DONE."));
    }

    [Fact]
    public void ExtractQuestions_FindsEachQuestionInOrder()
    {
        var questions = MultiQuestionDetector.ExtractQuestions(
            "What should the feature do? Also, who is the end user?");

        Assert.Equal(2, questions.Count);
        Assert.Contains("What should the feature do", questions[0]);
        Assert.Contains("who is the end user", questions[1]);
    }

    [Fact]
    public void ExtractQuestions_StripsListMarkers()
    {
        var questions = MultiQuestionDetector.ExtractQuestions(
            "1. How should 'add' be expressed? 2. What happens for invalid inputs?");

        Assert.Equal("How should 'add' be expressed", questions[0]);
        Assert.Equal("What happens for invalid inputs", questions[1]);
    }

    [Fact]
    public void ExtractQuestions_IgnoresToolEchoLines()
    {
        var questions = MultiQuestionDetector.ExtractQuestions("tool: execute\nWhat is your question?");

        Assert.Single(questions);
        Assert.Equal("What is your question", questions[0]);
    }

    [Fact]
    public void BuildCorrectionPrompt_QuotesTheOffendingQuestions()
    {
        var prompt = MultiQuestionDetector.BuildCorrectionPrompt(
            "What should it do? And who is the end user?");

        Assert.StartsWith(MultiQuestionDetector.CorrectionPromptPrefix, prompt);
        Assert.Contains("1) What should it do", prompt);
        Assert.Contains("2) ", prompt);
        Assert.Contains("who is the end user", prompt);
    }

    [Fact]
    public void BuildCorrectionPrompt_AsksForOnlyOneAndNotWhich()
    {
        var prompt = MultiQuestionDetector.BuildCorrectionPrompt("A? B?");

        Assert.Contains("Ask only ONE question", prompt);
        Assert.Contains("Do not ask which one", prompt);
    }

    [Fact]
    public void BuildCorrectionPrompt_FallsBackToQuotingTheWholeMessage()
    {
        // A single question mark in a long line leaves nothing to enumerate — quote the message.
        var prompt = MultiQuestionDetector.BuildCorrectionPrompt("Is this fine?");

        Assert.Contains("Is this fine?", prompt);
        Assert.Contains("Ask only ONE question", prompt);
    }

    [Fact]
    public void BuildCorrectionPrompt_CapsTheNumberOfQuotedQuestions()
    {
        var many = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"Question {i}?"));

        var prompt = MultiQuestionDetector.BuildCorrectionPrompt(many);

        Assert.Contains($"{MultiQuestionDetector.MaxQuotedQuestions}) Question {MultiQuestionDetector.MaxQuotedQuestions}", prompt);
        Assert.DoesNotContain($"{MultiQuestionDetector.MaxQuotedQuestions + 1}) Question", prompt);
    }

    [Fact]
    public void IsCorrectionPrompt_RecognisesABuiltPromptAndNothingElse()
    {
        Assert.True(MultiQuestionDetector.IsCorrectionPrompt(MultiQuestionDetector.BuildCorrectionPrompt("A? B?")));
        Assert.False(MultiQuestionDetector.IsCorrectionPrompt("What should the login form do?"));
        Assert.False(MultiQuestionDetector.IsCorrectionPrompt(null));
    }
}

public class MultiQuestionEnforcementTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly FakeGateRunner _gateRunner = new();
    private PersistingFakeCoordinator _coordinator = null!;

    public MultiQuestionEnforcementTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
        _coordinator = new PersistingFakeCoordinator(CreateFactory());
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task SendMessage_SingleQuestion_DoesNotCorrect()
    {
        _coordinator.UserTurnReply = "What should the login form do?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        Assert.DoesNotContain(_coordinator.Prompts, MultiQuestionDetector.IsCorrectionPrompt);
        Assert.Single(_coordinator.Prompts, p => p.StartsWith("We need a login form", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendMessage_MultiQuestion_CorrectsOnceAndLandsOnSingleQuestion()
    {
        _coordinator.UserTurnReply = "What should it do? And who is the end user?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var result = await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        Assert.Single(_coordinator.Prompts, MultiQuestionDetector.IsCorrectionPrompt);
        Assert.Equal("end_turn", result.Response);

        using var db = CreateFactory().CreateDbContext();
        var persisted = db.ReleaseStageRuns.Single(sr => sr.StageName == "business-analyst");
        Assert.Equal(1, persisted.QuestionCount);
        var sessionId = Guid.Parse(persisted.AcpSessionId!);
        var latestAssistant = db.Messages
            .Where(m => m.SessionId == sessionId && m.Role == "assistant" && m.BodyText != null)
            .ToList()
            .OrderByDescending(m => m.CreatedAt)
            .First().BodyText!;
        Assert.False(MultiQuestionDetector.ContainsMultipleQuestions(latestAssistant));
    }

    [Fact]
    public async Task SendMessage_RecordsATurnMetricWithKindStageAndTokens()
    {
        using var workspace = new TempWorkspace();
        _coordinator.UserTurnReply = "What should the login form do?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "It should log a user in", CancellationToken.None);

        using var db = CreateFactory().CreateDbContext();
        // Two Stage turns happen: StartStageAsync's opening prompt and this message. The opening
        // prompt carries a composition breakdown; the message turn does not.
        var stageTurns = db.TurnMetrics.Where(m => m.Kind == TurnKind.Stage).ToList();
        Assert.Equal(2, stageTurns.Count);
        Assert.All(stageTurns, metric => Assert.Equal("business-analyst", metric.StageName));
        Assert.Contains(stageTurns, metric => !string.IsNullOrEmpty(metric.PromptBreakdownJson));
    }

    [Fact]
    public async Task Interview_IsRecordedPerTurnAndReplayedToAFreshSession()
    {
        using var workspace = new TempWorkspace();
        _coordinator.UserTurnReply = "What should the login form do?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", workspace.Path, CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "It should log a user in", CancellationToken.None);

        var interviewPath = ArtifactPaths.InterviewPath(workspace.Path, "feat-001");
        Assert.True(File.Exists(interviewPath));
        var log = File.ReadAllText(interviewPath);
        Assert.Contains("What should the login form do", log);
        Assert.Contains("It should log a user in", log);

        // Move the BA stage on, then start it again: the run is no longer Active, so a brand-new
        // session is opened — it must be primed with the interview so far, not start over.
        await engine.RunGatesAsync(featureId, CancellationToken.None);
        _coordinator.Prompts.Clear();
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var prompt = _coordinator.Prompts.Last();
        Assert.Contains("Interview so far", prompt);
        Assert.Contains("It should log a user in", prompt);
    }

    [Fact]
    public async Task SendMessage_CorrectionQuotesTheOffendingQuestionsBack()
    {
        _coordinator.UserTurnReply = "What should it do? And who is the end user?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        var correction = Assert.Single(_coordinator.Prompts, MultiQuestionDetector.IsCorrectionPrompt);
        Assert.Contains("What should it do", correction);
        Assert.Contains("who is the end user", correction);
    }

    [Fact]
    public async Task SendMessage_EveryInteractiveTurnRestatesTheSingleQuestionRule()
    {
        _coordinator.UserTurnReply = "What should the login form do?";
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        var userTurn = Assert.Single(_coordinator.Prompts, p => p.StartsWith("We need a login form", StringComparison.Ordinal));
        Assert.Contains("Ask exactly ONE question", userTurn);
    }

    [Fact]
    public async Task SendMessage_StubbornMultiQuestion_CapsCorrections()
    {
        _coordinator.UserTurnReply = "Do you want A? Or B? Or C?";
        _coordinator.ReplyMultiQuestionToCorrections = true;
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        Assert.Equal(MultiQuestionDetector.MaxCorrections,
            _coordinator.Prompts.Count(MultiQuestionDetector.IsCorrectionPrompt));
    }

    [Fact]
    public async Task SendMessage_FinalResultComesFromLastPrompt()
    {
        _coordinator.UserTurnReply = "First question? Second question?";
        _coordinator.CorrectionReturn = new PromptResponse(
            Guid.NewGuid(), "max_calls_reached", 7, 3, 10);
        var engine = CreateEngine();
        var release = await engine.StartReleaseAsync("feat-001", @"C:\work\proj", CancellationToken.None);
        var featureId = release.CurrentFeatureId!.Value;
        await engine.StartStageAsync(featureId, CancellationToken.None);

        var result = await engine.SendMessageEnforcingSingleQuestionAsync(featureId, "We need a login form", CancellationToken.None);

        Assert.Equal("max_calls_reached", result.Response);
        Assert.Equal(10, result.TotalTokens);
    }

    private WorkflowEngine CreateEngine() => new(
        CreateFactory(), _gateRunner, _coordinator, _broadcaster,
        new FakeGitService(), new WorkflowDefinitionLoader(), NullLogger<WorkflowEngine>.Instance,
        new ModelCatalogService(_coordinator), new FakeGitCredentialStore(), new ActiveTurnTracker());

    private IDbContextFactory<DevTeamDbContext> CreateFactory()
        => new SqliteDbContextFactory(_connection);

    private sealed class SqliteDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}

internal sealed class PersistingFakeCoordinator : IWorkflowCoordinator
{
    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;

    public PersistingFakeCoordinator(IDbContextFactory<DevTeamDbContext> dbFactory) => _dbFactory = dbFactory;

    public List<string> Prompts { get; } = [];

    public string UserTurnReply { get; set; } = "What should the login form do?";

    public bool ReplyMultiQuestionToCorrections { get; set; }

    public PromptResponse? CorrectionReturn { get; set; }

    private long _tick;

    public Task<SessionSummary> NewSessionAsync(string workspacePath, string? modelId, IReadOnlyList<string>? allowedWritePrefixes, CancellationToken ct)
    {
        using var db = _dbFactory.CreateDbContext();
        var session = new DevTeamSession
        {
            WorkspacePath = workspacePath,
            AcpSessionId = "acp-" + Guid.NewGuid().ToString("N"),
            AllowedWritePrefixesJson = allowedWritePrefixes is null ? null : System.Text.Json.JsonSerializer.Serialize(allowedWritePrefixes),
        };
        db.Sessions.Add(session);
        db.SaveChanges();
        return Task.FromResult(new SessionSummary(
            session.Id, session.AcpSessionId, workspacePath, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], []));
    }

    public Task<string> SetModeAsync(Guid sessionId, string modeId, CancellationToken ct)
        => Task.FromResult(modeId);

    public Task<PromptResponse> PromptWithSessionRecoveryAsync(Guid sessionId, string text, CancellationToken ct, bool isPriming = false, string? displayText = null)
    {
        var isCorrection = MultiQuestionDetector.IsCorrectionPrompt(text);
        Prompts.Add(text);

        var reply = isCorrection
            ? ReplyMultiQuestionToCorrections
                ? "One question? A second? And a third?"
                : "What is the single most important thing to clarify?"
            : UserTurnReply;

        using var db = _dbFactory.CreateDbContext();
        db.Messages.Add(new Message
        {
            SessionId = sessionId,
            Role = "assistant",
            BodyText = reply,
            CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(++_tick),
        });
        db.SaveChanges();

        var result = isCorrection && CorrectionReturn is not null
            ? CorrectionReturn
            : new PromptResponse(sessionId, "end_turn", 10, 5, 15);
        return Task.FromResult(result);
    }
}