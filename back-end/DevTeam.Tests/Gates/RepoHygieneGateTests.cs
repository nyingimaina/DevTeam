using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Context;

namespace DevTeam.Tests.Gates;

public class RepoHygieneGateTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private GateRequest Request() => new(BuiltinRegistry.RepoHygiene, _workspace.Path, "feat-001");

    private string GitIgnorePath() => Path.Combine(_workspace.Path, ".gitignore");

    [Fact]
    public async Task CreatesGitIgnoreWhenMissing()
    {
        var gate = new RepoHygieneGate(FakeProcessRunner.Git(string.Empty));

        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        var text = File.ReadAllText(GitIgnorePath());
        Assert.Contains("bin/", text);
        Assert.Contains("obj/", text);
        Assert.Contains("*.log", text);
        Assert.Contains("node_modules/", text);
    }

    [Fact]
    public async Task MergesWithoutDuplicatingOrDroppingExistingEntries()
    {
        _workspace.Write(".gitignore", "bin/\nobj/\n# mine\n");
        var gate = new RepoHygieneGate(FakeProcessRunner.Git(string.Empty));

        await gate.RunAsync(Request(), CancellationToken.None);

        var lines = File.ReadAllLines(GitIgnorePath()).Select(line => line.Trim()).ToList();
        Assert.Single(lines.Where(line => line == "bin/"));
        Assert.Contains("# mine", lines);
        Assert.Contains("node_modules/", lines);
    }

    [Fact]
    public async Task UntracksIgnoredButTrackedFiles()
    {
        var runner = new FakeProcessRunner(request =>
            request.Arguments.StartsWith("ls-files", StringComparison.Ordinal)
                ? new ProcessRunResult(0, "back-end/src/bin/app.dll\nobj/x.pdb\n", string.Empty, false, TimeSpan.Zero)
                : new ProcessRunResult(0, string.Empty, string.Empty, false, TimeSpan.Zero));

        var result = await new RepoHygieneGate(runner).RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("untracked 2", result.EvidenceText);
        var removal = Assert.Single(runner.Calls.Where(call => call.Arguments.StartsWith("rm ", StringComparison.Ordinal)));
        Assert.Contains("app.dll", removal.Arguments);
        Assert.Contains("x.pdb", removal.Arguments);
    }

    [Fact]
    public async Task PassesAndStillWritesGitIgnoreWhenGitIsUnavailable()
    {
        var gate = new RepoHygieneGate(FakeProcessRunner.Git(string.Empty, exitCode: 128, error: "not a git repository"));

        var result = await gate.RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("bin/", File.ReadAllText(GitIgnorePath()));
    }
}

public class GitIgnoreTemplateTests
{
    [Fact]
    public void Merge_IsIdempotent()
    {
        var once = GitIgnoreTemplate.Merge(null);

        var twice = GitIgnoreTemplate.Merge(once);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void Merge_KeepsExistingContentAndAddsMissingLines()
    {
        var merged = GitIgnoreTemplate.Merge("bin/\n# keep me\n");

        Assert.Contains("# keep me", merged);
        Assert.Contains("node_modules/", merged);
        Assert.Single(merged.Split('\n').Where(line => line.Trim() == "bin/"));
    }

    [Fact]
    public void MissingLines_IgnoresCommentsAndIsCaseInsensitive()
    {
        var missing = GitIgnoreTemplate.MissingLines("BIN/\n# comment\n");

        Assert.DoesNotContain("bin/", missing);
        Assert.Contains("obj/", missing);
    }
}
