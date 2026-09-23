using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

/// <summary>
/// Regression tests for the bug that produced a dedicated project per feature: nothing ever
/// created the workspace's shared project, so the developer agent invented one inside its own
/// slice. <c>core_scaffold</c> must materialise exactly one shared backend project (and the
/// solution that references it) exactly once, and leave an imported workspace alone.
/// </summary>
public class CoreScaffoldGateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-core-" + Guid.NewGuid().ToString("N"));

    public CoreScaffoldGateTests() => Directory.CreateDirectory(_workspace);

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

    private SliceManifest WriteManifest(string codePathBack = "back-end/**/Features/<F>")
    {
        var manifest = new SliceManifest(
            "feat-001", "Login", codePathBack, "front-end/app/<F>", [], "dotnet test DevTeam.slnx");
        SliceManifestIO.Write(ArtifactPaths.ManifestPath(_workspace, "feat-001"), manifest);
        return manifest;
    }

    private Task<GateResult> RunAsync()
        => new CoreScaffoldGate().RunAsync(
            new GateRequest(BuiltinRegistry.CoreScaffold, _workspace, "feat-001", "business-analyst"),
            CancellationToken.None);

    private void Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private IReadOnlyList<string> Glob(string relativeDir, string pattern)
        => Directory.Exists(Path.Combine(_workspace, relativeDir))
            ? Directory.EnumerateFiles(
                    Path.Combine(_workspace, relativeDir.Replace('/', Path.DirectorySeparatorChar)),
                    pattern, SearchOption.AllDirectories)
                .Where(file => !file.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                    && !file.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(_workspace, file).Replace('\\', '/'))
                .ToArray()
            : [];

    [Fact]
    public async Task CreatesExactlyOneSharedProjectAndSolution()
    {
        WriteManifest();

        var result = await RunAsync();

        Assert.True(result.Passed, result.EvidenceText);
        Assert.True(File.Exists(Path.Combine(_workspace, "DevTeam.slnx")), "expected DevTeam.slnx at the workspace root");

        var projects = Glob("back-end/src", "*.csproj");
        Assert.Single(projects);
        var projectText = File.ReadAllText(Path.Combine(_workspace, projects[0].Replace('/', Path.DirectorySeparatorChar)));
        Assert.Contains("xunit", projectText);
        Assert.Contains("net10.0", projectText);

        var solution = File.ReadAllText(Path.Combine(_workspace, "DevTeam.slnx"));
        Assert.Contains(projects[0], solution);
    }

    [Fact]
    public async Task PointsTheFeatureSliceInsideTheSharedProject()
    {
        WriteManifest();

        await RunAsync();

        var manifest = SliceManifestIO.TryRead(ArtifactPaths.ManifestPath(_workspace, "feat-001"));
        Assert.NotNull(manifest);
        Assert.Equal("back-end/src/Features/<F>", manifest!.CodePathBack);
        Assert.Equal("back-end/src/Core", manifest.CorePathBack);
        Assert.Contains(manifest.Shared, shared => shared.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LeavesAnImportedWorkspaceUntouched()
    {
        WriteManifest();
        Write("back-end/src/Existing.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RunAsync();

        Assert.True(result.Passed);
        Assert.False(File.Exists(Path.Combine(_workspace, "DevTeam.slnx")), "must not scaffold over an existing project");
        Assert.Equal(["back-end/src/Existing.csproj"], Glob("back-end/src", "*.csproj"));
        Assert.Contains("Microsoft.NET.Sdk", File.ReadAllText(Path.Combine(_workspace, "back-end", "src", "Existing.csproj")));
    }

    [Fact]
    public async Task IsIdempotent()
    {
        WriteManifest();

        await RunAsync();
        var solutionAfterFirstRun = File.ReadAllText(Path.Combine(_workspace, "DevTeam.slnx"));
        await RunAsync();

        Assert.Single(Glob("back-end/src", "*.csproj"));
        Assert.Equal(solutionAfterFirstRun, File.ReadAllText(Path.Combine(_workspace, "DevTeam.slnx")));
    }

    [Fact]
    public async Task SkipsWithoutAFeatureManifest()
    {
        var result = await RunAsync();

        Assert.True(result.Passed);
        Assert.False(File.Exists(Path.Combine(_workspace, "DevTeam.slnx")));
    }

    [Fact]
    public async Task LeavesTheSharedCoreProjectAloneEvenWhenItIsListedAsShared()
    {
        // The core project is registered in `shared:` so the agent may edit it. The scaffold must
        // see it as "the workspace already has a project", not scaffold a duplicate next to it.
        var shared = "back-end/src/Core/App.csproj";
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/src/Features/<F>", "front-end/app/<F>", [shared], "dotnet test DevTeam.slnx"));
        Write(shared, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RunAsync();

        Assert.True(result.Passed, result.EvidenceText);
        Assert.False(File.Exists(Path.Combine(_workspace, "DevTeam.slnx")), "must not scaffold a duplicate project");
        Assert.Equal([shared], Glob("back-end/src", "*.csproj"));
    }

    [Fact]
    public void DeriveProjectName_UsesTheWorkspaceFolderAsAPascalCaseIdentifier()
    {
        Assert.Equal("Calculator", CoreScaffoldGate.DeriveProjectName(@"D:\apps\calculator"));
        Assert.Equal("MyShop", CoreScaffoldGate.DeriveProjectName("/home/dev/my-shop"));
        Assert.Equal("App", CoreScaffoldGate.DeriveProjectName(@"D:\apps\---"));
    }
}

/// <summary>
/// A feature slice holds feature-specific source files only. A build project belongs to the
/// shared app, so any project/solution file appearing under a feature's slice is a failure —
/// this is the deterministic guard that stops "one project per feature" from ever passing.
/// </summary>
public class ProjectStructureGateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-struct-" + Guid.NewGuid().ToString("N"));

    public ProjectStructureGateTests() => Directory.CreateDirectory(_workspace);

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

    private void WriteManifest(IReadOnlyList<string>? shared = null)
    {
        var manifest = new SliceManifest(
            "feat-001", "Login", "back-end/src/Features/<F>", "front-end/app/<F>", shared ?? [], "dotnet test DevTeam.slnx");
        SliceManifestIO.Write(ArtifactPaths.ManifestPath(_workspace, "feat-001"), manifest);
    }

    private void Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<GateResult> RunAsync()
        => new ProjectStructureGate().RunAsync(
            new GateRequest(BuiltinRegistry.ProjectStructure, _workspace, "feat-001", "developer"),
            CancellationToken.None);

    [Fact]
    public async Task Passes_WhenTheFeatureOnlyAddsSourceFiles()
    {
        WriteManifest();
        Write("back-end/src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("back-end/src/Features/feat-001/LoginHandler.cs", "namespace F; public sealed class LoginHandler { }");

        var result = await RunAsync();

        Assert.True(result.Passed, result.EvidenceText);
    }

    [Fact]
    public async Task Passes_WhenTheSharedProjectSitsOutsideTheSlice()
    {
        WriteManifest();
        Write("back-end/src/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("back-end/src/Features/feat-001/LoginHandler.cs", "namespace F; public sealed class LoginHandler { }");

        var result = await RunAsync();

        Assert.True(result.Passed, result.EvidenceText);
    }

    [Fact]
    public async Task Passes_WhenTheSharedCoreProjectIsListedAsASharedFile()
    {
        // Regression: the shared core project lives in `shared:`, and the old check treated the
        // shared list as part of the slice — so it flagged the one project we want.
        WriteManifest(shared: ["back-end/src/Core/App.csproj"]);
        Write("back-end/src/Core/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Write("back-end/src/Features/feat-001/LoginHandler.cs", "namespace F; public sealed class LoginHandler { }");

        var result = await RunAsync();

        Assert.True(result.Passed, result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheFeatureSliceContainsItsOwnProject()
    {
        WriteManifest();
        Write("back-end/src/Features/feat-001/Adding.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("Adding.csproj", result.EvidenceText);
    }

    [Fact]
    public async Task Fails_WhenTheFrontendSliceContainsItsOwnProject()
    {
        WriteManifest();
        Write("front-end/app/feat-001/Adding.App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("Adding.App.csproj", result.EvidenceText);
    }
}
