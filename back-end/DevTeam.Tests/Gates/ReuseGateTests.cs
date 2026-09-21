using DevTeam.Broker.Gates;

using DevTeam.Broker.Workflow;

namespace DevTeam.Tests.Gates;

public class CorePathsTests
{
    [Fact]
    public void Defaults_AreThePlatformConvention()
    {
        Assert.Equal("back-end/src/Core", CorePaths.DefaultBack);
        Assert.Equal("front-end/app/core", CorePaths.DefaultFront);
    }

    [Fact]
    public void FromSlices_UsesConfiguredOrDefaults()
    {
        var defaults = new WorkflowSlices(true, [], "devteam/features/<F>", "back-end/**/Features/<F>", "front-end/app/<F>");
        Assert.Equal("back-end/src/Core", CorePaths.Back(defaults));
        Assert.Equal("front-end/app/core", CorePaths.Front(defaults));

        var configured = defaults with { CoreBack = "back-end/Core", CoreFront = "web/core" };
        Assert.Equal("back-end/Core", CorePaths.Back(configured));
        Assert.Equal("web/core", CorePaths.Front(configured));
    }

    [Fact]
    public void ManifestPathsWin_OverSlices()
    {
        var manifest = new SliceManifest("f", "t", "back", "front", [], "t", "custom-back", "custom-front");
        var slices = new WorkflowSlices(true, [], "a", "b", "c", "slices-back", "slices-front");

        var (back, front) = CorePaths.Resolve(slices, manifest);

        Assert.Equal("custom-back", back);
        Assert.Equal("custom-front", front);
    }
}

public class ReuseGateTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-reuse-" + Guid.NewGuid().ToString("N"));

    public ReuseGateTests() => Directory.CreateDirectory(_workspace);

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

    private SliceManifest WriteManifest(string coreBack = "back-end/src/Core", string coreFront = "front-end/app/core")
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            coreBack, coreFront);
        SliceManifestIO.Write(ArtifactPaths.ManifestPath(_workspace, "feat-001"), manifest);
        return manifest;
    }

    private void Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private Task<GateResult> RunAsync()
        => new ReuseGate().RunAsync(new GateRequest(BuiltinRegistry.ReuseGate, _workspace, "feat-001"), CancellationToken.None);

    [Fact]
    public async Task NoCoreDirectories_Passes()
    {
        WriteManifest();
        Write("back-end/Features/feat-001/TodoHandler.cs", "namespace F; public sealed class TodoHandler { }");

        var result = await RunAsync();

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task FeatureExtendingCoreWithNewFiles_Passes()
    {
        WriteManifest();
        Write("back-end/src/Core/TodoService.cs", "namespace Core; public sealed class TodoService { }");
        Write("back-end/Features/feat-001/TodoFeatures.cs", "namespace F; public sealed class TodoFeatures { }");
        Write("front-end/app/core/page.tsx", "export default function CorePage() { return null; }");
        Write("front-end/app/feat-001/page.tsx", "export default function FeaturePage() { return null; }");

        var result = await RunAsync();

        Assert.True(result.Passed);
    }

    [Fact]
    public async Task DuplicateBackendType_Fails()
    {
        WriteManifest();
        Write("back-end/src/Core/TodoService.cs", "namespace Core; public sealed class TodoService { }");
        Write("back-end/Features/feat-001/TodoService.cs", "namespace F; public sealed class TodoService { }");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("shadow-declares", result.EvidenceText);
        Assert.Contains("TodoService.cs", result.EvidenceText);
    }

    [Fact]
    public async Task DuplicateFrontendComponent_Fails()
    {
        WriteManifest();
        Write("front-end/app/core/TodoList.tsx", "export default function TodoList() { return null; }");
        Write("front-end/app/feat-001/TodoList.tsx", "export default function TodoList() { return null; }");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("TodoList.tsx", result.EvidenceText);
    }

    [Fact]
    public async Task ScaffoldedSiblingProject_Fails()
    {
        WriteManifest();
        Write("back-end/src/Core/TodoApp.Core.csproj", "<Project />");
        Write("back-end/Features/feat-001/TodoApp.Core.csproj", "<Project />");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("duplicate project", result.EvidenceText);
    }

    [Fact]
    public async Task CustomCorePaths_AreRespected()
    {
        WriteManifest(coreBack: "back-end/Core", coreFront: "web/core");
        Write("back-end/Core/TodoService.cs", "namespace Core; public sealed class TodoService { }");
        Write("back-end/Features/feat-001/TodoService.cs", "namespace F; public sealed class TodoService { }");

        var result = await RunAsync();

        Assert.False(result.Passed);
        Assert.Contains("back-end/Core", result.EvidenceText);
    }
}