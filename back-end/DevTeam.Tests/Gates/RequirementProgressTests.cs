using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Context;

namespace DevTeam.Tests.Gates;

public class RequirementMatcherTests
{
    [Theory]
    [InlineData("REQ_1_AddWorks")]
    [InlineData("REQ001_AddWorks")]
    [InlineData("REQ 1 add works")]
    [InlineData("req-1")]
    public void MatchesId_AcceptsTheSpellingsDevelopersUse(string corpus)
    {
        Assert.True(RequirementMatcher.MatchesId(corpus, "REQ-001"));
    }

    [Fact]
    public void MatchesId_DoesNotLetRequirementOneBeMatchedByTen()
    {
        Assert.False(RequirementMatcher.MatchesId("REQ_10_TenWorks", "REQ-1"));
        Assert.True(RequirementMatcher.MatchesId("REQ_10_TenWorks", "REQ-10"));
    }

    [Fact]
    public void MatchesRequirement_DoesNotMatchOnTheTitleWordAlone()
    {
        var requirement = new RequirementDtos.Requirement("REQ-7", "Add", "Given a, When b, Then c");

        // A test file that merely contains the word "Add" must not count as covering REQ-7.
        Assert.False(RequirementMatcher.MatchesRequirement("SomeAddButton_Works", requirement));
        Assert.True(RequirementMatcher.MatchesRequirement("REQ_7_Add_Works", requirement));
    }
}

public class RequirementProgressTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private void WriteBrs()
        => _workspace.Write(
            "devteam/features/feat-001/BRS.md",
            "# feat\n\n" +
            "## REQ-1: Add\nGiven a, When b, Then c\n\n" +
            "## REQ-2: Subtract\nGiven a, When b, Then c\n\n" +
            "## REQ-3: Clear\nGiven a, When b, Then c\n");

    [Fact]
    public void Compute_CountsTaggedCodeAndNamedTests()
    {
        WriteBrs();
        _workspace.Write("src/Calculator.cs", "// REQ-1\npublic int Add(int a, int b) => a + b;\n");
        _workspace.Write("src/Subtract.cs", "// REQ-2\npublic int Sub(int a, int b) => a - b;\n");
        _workspace.Write("src/CalculatorTests.cs", "REQ_1_Adds; REQ_2_Subtracts; REQ_3_Clears;\n");

        var progress = RequirementProgressCalculator.Compute(_workspace.Path, "feat-001");

        Assert.Equal(3, progress.Requirements);
        Assert.Equal(new ProgressCount(2, 3), progress.Code);
        Assert.Equal(new ProgressCount(3, 3), progress.Tests);
    }

    [Fact]
    public void Compute_DoesNotCountTestFilesAsCode()
    {
        WriteBrs();
        // Only a test names REQ-1 — that is test progress, not code progress.
        _workspace.Write("src/CalculatorTests.cs", "REQ_1_Adds;\n");

        var progress = RequirementProgressCalculator.Compute(_workspace.Path, "feat-001");

        Assert.Equal(0, progress.Code.Done);
        Assert.Equal(1, progress.Tests.Done);
    }

    [Fact]
    public void Compute_TreatsABareReqLineTheSameAsAProperHeading()
    {
        // Regression: a BRS with a REQ that's missing its "##" markdown marker used to be
        // silently absorbed into the previous requirement's body instead of counted at all —
        // the progress bars stayed permanently empty for a feature whose BA dropped the marker
        // on even one requirement.
        _workspace.Write(
            "devteam/features/feat-001/BRS.md",
            "# feat\n\n" +
            "## REQ-1: Add\nGiven a, When b, Then c\n\n" +
            "REQ-2: Subtract\nGiven a, When b, Then c\n");
        _workspace.Write("src/Calculator.cs", "// REQ-1\npublic int Add(int a, int b) => a + b;\n// REQ-2\n");

        var progress = RequirementProgressCalculator.Compute(_workspace.Path, "feat-001");

        Assert.Equal(2, progress.Requirements);
        Assert.Equal(new ProgressCount(2, 2), progress.Code);
    }

    [Fact]
    public void Compute_IsEmptyWithoutARequirementList()
    {
        var progress = RequirementProgressCalculator.Compute(_workspace.Path, "feat-001");

        Assert.Equal(0, progress.Requirements);
        Assert.Equal(0, progress.Code.Total);
        Assert.Equal(0, progress.Tests.Total);
    }

    [Fact]
    public void Compute_DoesNotBorrowAnotherFeaturesTestsOrCode()
    {
        // Regression: requirement ids are per-feature (every BRS starts at REQ-1), so a
        // workspace-wide scan let one feature's files satisfy another feature's requirements.
        _workspace.Write("devteam/features/alpha/BRS.md", "# alpha\n\n## REQ-1: Add\nGiven a, When b, Then c\n");
        _workspace.Write("devteam/features/beta/BRS.md", "# beta\n\n## REQ-1: Launch\nGiven a, When b, Then c\n");
        WriteManifest("alpha");
        WriteManifest("beta");
        _workspace.Write("back-end/src/Features/alpha/AlphaTests.cs", "REQ_1_Adds;\n");
        _workspace.Write("back-end/src/Features/alpha/Alpha.cs", "// REQ-1\n");

        var beta = RequirementProgressCalculator.Compute(_workspace.Path, "beta");
        var alpha = RequirementProgressCalculator.Compute(_workspace.Path, "alpha");

        Assert.Equal(0, beta.Tests.Done);
        Assert.Equal(0, beta.Code.Done);
        Assert.Equal(1, alpha.Tests.Done);
        Assert.Equal(1, alpha.Code.Done);
    }

    private void WriteManifest(string feature)
        => SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace.Path, feature),
            new SliceManifest(feature, feature, "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test DevTeam.slnx"));
}

public class FeatureTestFilesTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Discover_OnlyReturnsTestsInsideTheFeatureSlice()
    {
        _workspace.Write("back-end/src/Features/alpha/AlphaTests.cs", "REQ_1_Adds;\n");
        _workspace.Write("back-end/src/Features/beta/BetaTests.cs", "REQ_1_Launches;\n");
        var manifest = new SliceManifest("alpha", "alpha", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test");

        var alpha = FeatureTestFiles.Discover(_workspace.Path, "alpha", manifest);

        var only = Assert.Single(alpha);
        Assert.Contains("AlphaTests.cs", only.Path);
    }

    [Fact]
    public void Discover_WithNoManifest_FallsBackToTheWholeWorkspace()
    {
        _workspace.Write("back-end/src/Features/alpha/AlphaTests.cs", "REQ_1_Adds;\n");

        var files = FeatureTestFiles.Discover(_workspace.Path, "alpha", null);

        Assert.Single(files);
    }
}
