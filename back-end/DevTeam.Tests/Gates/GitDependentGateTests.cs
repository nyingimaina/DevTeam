using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Func<ProcessRunRequest, ProcessRunResult> _handler;

    public FakeProcessRunner(Func<ProcessRunRequest, ProcessRunResult> handler) => _handler = handler;

    public List<ProcessRunRequest> Calls { get; } = [];

    public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken)
    {
        Calls.Add(request);
        return Task.FromResult(_handler(request));
    }

    public static FakeProcessRunner Git(string output, int exitCode = 0, string error = "")
        => new(_ => new ProcessRunResult(exitCode, output, error, false, TimeSpan.Zero));
}

public class GitDependentGateTests
{
    private static readonly string Workspace = @"C:\work\proj";

    [Fact]
    public async Task Hygiene_PassesOnCleanDiff()
    {
        var runner = FakeProcessRunner.Git("+++ b/Program.cs\n+Console.WriteLine(\"ok\");\n");
        var gate = new CodeHygieneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.CodeHygiene, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData("+// TODO: fix later", "TODO")]
    [InlineData("+var password = \"hunter2\";", "password")]
    [InlineData("+const apiKey = \"abc123\";", "apiKey")]
    public async Task Hygiene_FlagsBannedContentInAddedLines(string addedLine, string expectedToken)
    {
        var runner = FakeProcessRunner.Git($"+++ b/Server/Config.cs\n+Console.WriteLine(1);\n{addedLine}\n");
        var gate = new CodeHygieneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.CodeHygiene, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains(expectedToken, result.EvidenceText);
        Assert.Contains("Server/Config.cs", result.EvidenceText);
    }

    [Theory]
    [InlineData("Server/Config.py", "+# TODO: handle retries")]
    [InlineData("Server/index.html", "+<!-- TODO: update copy -->")]
    public async Task Hygiene_StillFlagsMarkersInNonCStyleComments(string file, string addedLine)
    {
        var runner = FakeProcessRunner.Git($"+++ b/{file}\n{addedLine}\n");
        var gate = new CodeHygieneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.CodeHygiene, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("TODO", result.EvidenceText);
    }

    [Theory]
    [InlineData("+namespace Todo.Api;")]
    [InlineData("+public class TodoController")]
    [InlineData("+// see the todo item below")]
    [InlineData("+const todoLabel = \"TODO\";")]
    public async Task Hygiene_DoesNotFlagCoincidentalKeywordMatchesFromTheAppsOwnName(string addedLine)
    {
        var runner = FakeProcessRunner.Git($"+++ b/Server/Config.cs\n+Console.WriteLine(1);\n{addedLine}\n");
        var gate = new CodeHygieneGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.CodeHygiene, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task SliceGuard_RejectsFilesOutsideAllowlist()
    {
        var workspace = CreateWorkspaceWithManifest(featureKey: "feat-001");
        var runner = FakeProcessRunner.Git("back-end/Features/Login/Ctrl.cs\nfront-end/app/home/page.tsx\n");
        var gate = new SliceGuardGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceGuard, workspace, "feat-001", "developer"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("front-end/app/home/page.tsx", result.EvidenceText);
        Assert.DoesNotContain("back-end/Features/Login/Ctrl.cs", result.EvidenceText);
    }

    [Fact]
    public async Task SliceGuard_PassesWhenOnlyAllowedFilesChanged()
    {
        var workspace = CreateWorkspaceWithManifest(featureKey: "feat-001");
        var runner = FakeProcessRunner.Git("back-end/Features/Login/Ctrl.cs\nProgram.cs\n");
        var gate = new SliceGuardGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceGuard, workspace, "feat-001", "developer"),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task RenderPr_WritesSummaryWithChangedFiles()
    {
        var workspace = CreateWorkspaceWithManifest(featureKey: "feat-001");
        var runner = FakeProcessRunner.Git("back-end/Features/Login/Ctrl.cs");
        var gate = new RenderPrGate(runner);

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.RenderPr, workspace, "feat-001", "developer", new Dictionary<string, string>
            {
                ["testOutput"] = "tests: 3 passed, 0 failed",
                ["coverage"] = "| REQ-001 | covered |",
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        var prPath = Path.Combine(workspace, "devteam", "features", "feat-001", "PR-feat-001.md");
        Assert.True(File.Exists(prPath));
        var content = File.ReadAllText(prPath);
        Assert.Contains("back-end/Features/Login/Ctrl.cs", content);
        Assert.Contains("tests: 3 passed, 0 failed", content);
        Assert.Contains("REQ-001", content);
    }

    [Fact]
    public async Task RenderHandoff_WritesHandoffWithArtifacts()
    {
        var workspace = CreateWorkspaceWithManifest(featureKey: "feat-001");
        var gate = new RenderHandoffGate();

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.RenderHandoff, workspace, "feat-001", "developer", new Dictionary<string, string>
            {
                ["nextStage"] = "qa",
                ["summary"] = "Login implemented with tests.",
            }),
            CancellationToken.None);

        Assert.True(result.Passed);
        var handoff = File.ReadAllText(ArtifactPaths.HandoffPath(workspace, "feat-001"));
        Assert.Contains("Next stage: qa", handoff);
        Assert.Contains("manifest.yaml", handoff);
    }

    private static string CreateWorkspaceWithManifest(string featureKey)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "devteam-slice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ArtifactPaths.FeatureDir(workspace, featureKey));
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(workspace, featureKey),
            new SliceManifest(
                featureKey,
                "Login",
                "back-end/**/Features/Login",
                "front-end/app/login",
                ["Program.cs", "DevTeamDbContext.cs"],
                "dotnet test DevTeam.slnx"));
        return workspace;
    }
}