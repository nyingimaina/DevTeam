using DevTeam.Broker.Gates;
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
    public void Load_ProjectProfileWithBuildAndTestCommands_GeneratesPhasesFromIt()
    {
        // No .slnx/front-end here — convention-sniffing would find nothing. A workspace-level
        // project-profile.yaml (written once by scaffold_specs the first time a feature declares
        // a projectType/free-form hierarchy) should generate phases from its own declared
        // buildCommand/testCommand instead of leaving readiness empty.
        var workspace = NewWorkspace();
        ProjectProfileIO.Write(workspace, new ProjectProfile
        {
            ProjectType = "wpf-desktop",
            BuildCommand = "dotnet build Calculator.slnx",
            TestCommand = "dotnet test Calculator.slnx",
        });

        var profile = ReadinessProfileLoader.Load(workspace);

        var build = Assert.Single(profile.Phases, p => p.Id == "project-build");
        Assert.Equal("dotnet build Calculator.slnx", build.Command);
        var tests = Assert.Single(profile.Phases, p => p.Id == "project-tests");
        Assert.Equal("dotnet test Calculator.slnx", tests.Command);
        Assert.Contains("project-build", tests.DependsOn!);
    }

    [Fact]
    public void Load_ProjectProfileWithNoBuildOrTestCommand_FallsThroughToConventionSniffing()
    {
        // A profile can exist purely to declare a projectType/corePaths with no build/test
        // command at all — that must not silently produce an always-passing empty readiness
        // profile on a workspace that convention-sniffing would otherwise recognise.
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<Solution />");
        ProjectProfileIO.Write(workspace, new ProjectProfile { ProjectType = "wpf-desktop" });

        var profile = ReadinessProfileLoader.Load(workspace);

        Assert.Contains(profile.Phases, p => p.Id == "backend-build");
    }

    [Fact]
    public void Load_ReadinessYamlOverride_WinsOverProjectProfile()
    {
        // The full three-way precedence: readiness.yaml > project-profile.yaml > convention.
        var workspace = NewWorkspace();
        ProjectProfileIO.Write(workspace, new ProjectProfile
        {
            ProjectType = "wpf-desktop",
            BuildCommand = "dotnet build Calculator.slnx",
            TestCommand = "dotnet test Calculator.slnx",
        });
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
    public void Load_NoOverrideAndNoProjectProfile_FallsBackToConventionSniffingUnchanged()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "DevTeam.slnx"), "<Solution />");

        var profile = ReadinessProfileLoader.Load(workspace);

        Assert.Contains(profile.Phases, p => p.Id == "backend-build");
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
