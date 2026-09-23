using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class ChangedFileParserTests
{
    [Fact]
    public void Parse_YieldsPathPortionOfEachStatusLine()
    {
        var lines = ChangedFileParser.Parse("M  Program.cs\n?? back-end/src/Core/Models/Todo.cs\n").ToArray();

        Assert.Equal(["Program.cs", "back-end/src/Core/Models/Todo.cs"], lines);
    }

    [Fact]
    public void Parse_RenameStatus_KeepsTheRenameTarget()
    {
        var lines = ChangedFileParser.Parse("R  old/Name.cs -> new/Name.cs\n").ToArray();

        Assert.Equal(["new/Name.cs"], lines);
    }

    [Fact]
    public void Parse_IgnoresBlankAndStatusOnlyLines()
    {
        var lines = ChangedFileParser.Parse("\n\n").ToArray();

        Assert.Empty(lines);
    }
}

public class SliceScopeGateTests
{
    private const string Workspace = @"C:\work\proj";

    private static SliceScopeGate GateReturning(string gitStatusOutput)
        => new(new FakeProcessRunner(_ => new ProcessRunResult(0, gitStatusOutput, "", false, TimeSpan.Zero)));

    private static readonly IReadOnlyDictionary<string, string> Slice =
        new Dictionary<string, string> { ["codePaths"] = "back-end/Features/feat-001;front-end/app/feat-001" };

    [Fact]
    public async Task UntrackedFileOutsideSliceAndCore_Fails()
    {
        var gate = GateReturning("?? back-end/CloneOfTheApp/Secret.cs\n");

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceScope, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("back-end/CloneOfTheApp/Secret.cs", result.EvidenceText);
    }

    [Fact]
    public async Task PlaceholderFilesInsideTheSlice_AreNotFlagged()
    {
        var gate = GateReturning("?? front-end/app/feat-001/.placeholder\n?? devteam/features/feat-001/requirements.md\n");

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceScope, Workspace, "feat-001", "developer", Slice),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task UntrackedFilesInsideSliceAndCore_Pass()
    {
        var gate = GateReturning("?? back-end/src/Core/TodoService.cs\n?? back-end/Features/feat-001/TodoHandler.cs\n");

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceScope, Workspace, "feat-001", "developer", Slice),
            CancellationToken.None);

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task FrozenScopeSnapshotExists_ChecksAgainstItInsteadOfALiveManifestTheDeveloperWidened()
    {
        // The developer has write access to its own manifest.yaml (legitimate reasons — free-form
        // hierarchies) — this proves slice_scope can't be defeated by editing codePaths/Shared to
        // include wherever it already wrote once the frozen snapshot exists.
        var dir = Directory.CreateTempSubdirectory("slice-scope-frozen-");
        try
        {
            SliceManifestIO.Write(
                ArtifactPaths.ManifestPath(dir.FullName, "feat-001"),
                new SliceManifest(
                    "feat-001", "Login", "some/unrelated/folder", "another/unrelated/folder", ["secrets.txt"], "dotnet test"));
            DeveloperScopeSnapshotIO.WriteIfAbsent(dir.FullName, "feat-001",
                new DeveloperScopeSnapshot(["back-end/Features/feat-001"], "back-end/src/Core", "", []));

            var gate = GateReturning("?? some/unrelated/folder/Stolen.cs\n");
            var result = await gate.RunAsync(
                new GateRequest(BuiltinRegistry.SliceScope, dir.FullName, "feat-001", "developer"),
                CancellationToken.None);

            Assert.False(result.Passed);
            Assert.Contains("some/unrelated/folder/Stolen.cs", result.EvidenceText);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFeatureKey_Fails()
    {
        var gate = GateReturning("");
        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceScope, Workspace, ""),
            CancellationToken.None);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task GitStatusFailure_Fails()
    {
        var gate = new SliceScopeGate(new FakeProcessRunner(_ => new ProcessRunResult(128, "", "fatal: not a git repository", false, TimeSpan.Zero)));

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.SliceScope, Workspace, "feat-001"),
            CancellationToken.None);

        Assert.False(result.Passed);
    }

    [Fact]
    public async Task InvokesGitStatusWithUntrackedFilesAll()
    {
        // Plain `git status --porcelain` collapses a brand-new, entirely-untracked directory
        // (e.g. a feature's first subfolder) into a single "?? path/to/dir/" line instead of
        // listing the files inside it. That collapsed line then fails the slice-template match
        // (which expects "path/to/dir/<feature>/File.cs"), so the gate must ask git to expand
        // every file individually.
        var runner = new FakeProcessRunner(_ => new ProcessRunResult(0, "", "", false, TimeSpan.Zero));
        var gate = new SliceScopeGate(runner);

        await gate.RunAsync(new GateRequest(BuiltinRegistry.SliceScope, Workspace, "feat-001"), CancellationToken.None);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("git", call.FileName);
        Assert.Contains("--untracked-files=all", call.Arguments);
    }

    [Fact]
    public async Task RealGit_NewFeatureSubdirectoryWithNestedFiles_IsNotFlaggedAsOutOfSlice()
    {
        var dir = Directory.CreateTempSubdirectory("slice-scope-real-git-");
        try
        {
            var runner = new SystemProcessRunner();
            await runner.RunAsync(new ProcessRunRequest("git", "init", dir.FullName), CancellationToken.None);
            await runner.RunAsync(new ProcessRunRequest("git", "config user.email test@example.com", dir.FullName), CancellationToken.None);
            await runner.RunAsync(new ProcessRunRequest("git", "config user.name Test", dir.FullName), CancellationToken.None);

            var featureDir = Path.Combine(dir.FullName, "back-end", "src", "Features", "addition");
            Directory.CreateDirectory(featureDir);
            await File.WriteAllTextAsync(Path.Combine(featureDir, "MainViewModel.cs"), "// vm");
            await File.WriteAllTextAsync(Path.Combine(featureDir, "MainWindow.xaml.cs"), "// window");

            var slice = new Dictionary<string, string> { ["codePaths"] = "back-end/src/Features/<F>" };
            var gate = new SliceScopeGate(runner);

            var result = await gate.RunAsync(
                new GateRequest(BuiltinRegistry.SliceScope, dir.FullName, "addition", "developer", slice),
                CancellationToken.None);

            Assert.True(result.Passed, result.EvidenceText);
        }
        finally
        {
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}