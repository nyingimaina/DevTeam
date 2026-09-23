using DevTeam.Broker.Gates.Readiness;

namespace DevTeam.Tests.Gates.Readiness;

public class ReadinessProfileTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void Detect_FindsBackendAndFrontendChecksFromTheProjectsShape()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(workspace, "front-end"));
        File.WriteAllText(Path.Combine(workspace, "front-end", "package.json"), "{}");

        var profile = ReadinessProfileDetector.Detect(workspace);
        var ids = profile.Phases.Select(p => p.Id).ToList();

        Assert.Equal(
            ["backend-build", "backend-unit", "backend-integration",
             "frontend-typecheck", "frontend-build", "frontend-lint", "frontend-tests"],
            ids);
        Assert.Equal("front-end", profile.Phases.Single(p => p.Id == "frontend-tests").WorkingDirectory);
        Assert.Contains("backend-build", profile.Phases.Single(p => p.Id == "backend-unit").DependsOn!);
    }

    [Fact]
    public void Detect_TreatsARootPackageJsonAsAFrontendAtTheRoot()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "package.json"), "{}");

        var profile = ReadinessProfileDetector.Detect(workspace);

        Assert.All(profile.Phases, phase => Assert.Null(phase.WorkingDirectory));
        Assert.Contains(profile.Phases, p => p.Id == "frontend-tests");
    }

    [Fact]
    public void Detect_IsEmptyForAWorkspaceWithNoRecognisableProject()
    {
        var profile = ReadinessProfileDetector.Detect(NewWorkspace());

        Assert.Empty(profile.Phases);
    }

    [Fact]
    public void Load_PrefersAHandAuthoredOverride()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<Solution />");
        Directory.CreateDirectory(Path.Combine(workspace, "devteam"));
        File.WriteAllText(Path.Combine(workspace, "devteam", "readiness.yaml"), """
        phases:
          - id: smoke
            title: The smoke test passes
            command: echo ok
        """);

        var profile = ReadinessProfileLoader.Load(workspace);

        Assert.Equal(["smoke"], profile.Phases.Select(p => p.Id));
    }

    [Fact]
    public void FromYaml_AppliesDefaultsAndParsesCoverage()
    {
        var profile = ReadinessProfileLoader.FromYaml("""
        phases:
          - id: tests
            title: The tests pass
            command: npx jest
            timeoutMs: 0
            dependsOn:
              - build
            coverage:
              relativeJsonPath: coverage/coverage-summary.json
              lineMin: 80
        """);

        var phase = Assert.Single(profile.Phases);
        Assert.Equal("The tests pass", phase.Title);
        Assert.Equal(ReadinessDefaults.PhaseTimeoutMs, phase.TimeoutMs);
        Assert.True(phase.Required);
        Assert.Equal(["build"], phase.DependsOn!);
        Assert.Equal(80, phase.Coverage!.LineMin);
    }

    private string NewWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "devteam-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempDirectories.Add(path);
        return path;
    }
}
