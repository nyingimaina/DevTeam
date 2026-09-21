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
}