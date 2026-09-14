using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class RequirementsExtractorTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-reqext-" + Guid.NewGuid().ToString("N"));

    private string RequirementsPath => ArtifactPaths.RequirementsPath(_workspace, "feat-001");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void Seed(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(RequirementsPath)!);
        File.WriteAllText(RequirementsPath, content);
    }

    [Fact]
    public void Extract_MissingFile_ReturnsEmpty()
    {
        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        Assert.Empty(requirements);
    }

    [Fact]
    public void Extract_ScaffoldPlaceholder_ReturnsEmpty()
    {
        Seed("# feat-001 — Login" + Environment.NewLine +
             Environment.NewLine +
             "(No requirements yet — add REQ entries below, each with Gherkin acceptance criteria.)");

        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        Assert.Empty(requirements);
    }

    [Fact]
    public void Extract_ParsesRequirementsWithAcceptanceCriteria()
    {
        Seed("## REQ-1: User can log in" + Environment.NewLine +
             "Given a registered user" + Environment.NewLine +
             "When they enter valid credentials" + Environment.NewLine +
             "Then they are signed in" + Environment.NewLine +
             Environment.NewLine +
             "## REQ-2: User can log out" + Environment.NewLine +
             "Given a signed-in user" + Environment.NewLine +
             "When they click logout" + Environment.NewLine +
             "Then they are signed out");

        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        Assert.Equal(2, requirements.Count);
        Assert.Equal("REQ-1", requirements[0].Id);
        Assert.Equal("User can log in", requirements[0].Title);
        Assert.Contains("When they enter valid credentials", requirements[0].AcceptanceCriteria);
        Assert.Equal("REQ-2", requirements[1].Id);
        Assert.Contains("Then they are signed out", requirements[1].AcceptanceCriteria);
    }

    [Fact]
    public void Extract_KeepsSubsectionHeadingsWithinAcceptance()
    {
        Seed("## REQ-5: Password rules" + Environment.NewLine +
             "Given a new user" + Environment.NewLine +
             "### Constraints" + Environment.NewLine +
             "When they pick a password shorter than 8 chars" + Environment.NewLine +
             "Then they get a validation error");

        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        var acceptance = Assert.Single(requirements).AcceptanceCriteria;
        Assert.Contains("### Constraints", acceptance);
        Assert.DoesNotContain("## REQ-5", acceptance);
    }

    [Fact]
    public void Extract_SkipsProseHeadingsWithoutAnId()
    {
        Seed("## Overview" + Environment.NewLine +
             "This feature covers account management." + Environment.NewLine +
             Environment.NewLine +
             "## REQ-3: Reset password" + Environment.NewLine +
             "Given a registered user" + Environment.NewLine +
             "When they request a reset" + Environment.NewLine +
             "Then they receive an email");

        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        var requirement = Assert.Single(requirements);
        Assert.Equal("REQ-3", requirement.Id);
        Assert.DoesNotContain("Overview", requirement.AcceptanceCriteria);
    }

    [Fact]
    public void Extract_TrimsLeadingAndTrailingBlankLinesFromAcceptance()
    {
        Seed("## REQ-4: Empty state" + Environment.NewLine +
             Environment.NewLine +
             "   Given an empty list" + Environment.NewLine +
             "   Then a friendly empty state is shown" + Environment.NewLine +
             Environment.NewLine);

        var requirements = RequirementsExtractor.Extract(_workspace, "feat-001");

        var acceptance = Assert.Single(requirements).AcceptanceCriteria;
        Assert.StartsWith("Given an empty list", acceptance);
        Assert.EndsWith("shown", acceptance);
    }
}