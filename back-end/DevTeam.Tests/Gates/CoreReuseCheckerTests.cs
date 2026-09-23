using DevTeam.Broker.Gates;

namespace DevTeam.Tests.Gates;

public class CoreReuseCheckerTests : IDisposable
{
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "devteam-core-reuse-" + Guid.NewGuid().ToString("N"));

    public CoreReuseCheckerTests() => Directory.CreateDirectory(_workspace);

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

    private void Write(string relativePath, string content = "")
    {
        var path = Path.Combine(_workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void FindViolations_NoManifest_ReturnsEmpty()
    {
        Assert.Empty(CoreReuseChecker.FindViolations(_workspace, null));
    }

    [Fact]
    public void FindViolations_DuplicateBackendType_ReportsShadowDeclare()
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            "back-end/src/Core", "front-end/app/core");
        Write("back-end/src/Core/TodoService.cs", "namespace Core; public sealed class TodoService { }");
        Write("back-end/Features/feat-001/TodoService.cs", "namespace F; public sealed class TodoService { }");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Contains(violations, v => v.Contains("shadow-declares") && v.Contains("TodoService.cs"));
    }

    [Fact]
    public void FindViolations_DuplicateCsproj_ReportsDuplicateProject()
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            "back-end/src/Core", "front-end/app/core");
        Write("back-end/src/Core/TodoApp.Core.csproj", "<Project />");
        Write("back-end/Features/feat-001/TodoApp.Core.csproj", "<Project />");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Contains(violations, v => v.Contains("duplicate project"));
    }

    [Fact]
    public void FindViolations_DuplicateFrontendComponent_ReportsShadow()
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            "back-end/src/Core", "front-end/app/core");
        Write("front-end/app/core/TodoList.tsx", "export default function TodoList() { return null; }");
        Write("front-end/app/feat-001/TodoList.tsx", "export default function TodoList() { return null; }");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Contains(violations, v => v.Contains("TodoList.tsx"));
    }

    [Fact]
    public void FindViolations_GenericNextJsRouteFileName_NotAViolation()
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            "back-end/src/Core", "front-end/app/core");
        Write("front-end/app/core/page.tsx", "export default function CorePage() { return null; }");
        Write("front-end/app/feat-001/page.tsx", "export default function FeaturePage() { return null; }");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Empty(violations);
    }

    [Fact]
    public void FindViolations_FeatureExtendingCoreWithNewNames_ReturnsNoViolations()
    {
        var manifest = new SliceManifest(
            "feat-001", "Todo", "back-end/Features/feat-001", "front-end/app/feat-001", [], "dotnet test",
            "back-end/src/Core", "front-end/app/core");
        Write("back-end/src/Core/TodoService.cs", "namespace Core; public sealed class TodoService { }");
        Write("back-end/Features/feat-001/TodoFeatures.cs", "namespace F; public sealed class TodoFeatures { }");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Empty(violations);
    }

    [Fact]
    public void FindViolations_FreeFormManifestWithThirdCodePathSlot_StillChecksFilesInThatSlot()
    {
        // Regression: a free-form manifest with more than two declared code paths used to only
        // ever check the first two (CodePathBack/CodePathFront) against the core — a duplicate
        // sitting in a third (or later) declared path went completely undetected. Route through
        // EffectiveCodePaths so every declared path is checked, not just the first two.
        var manifest = new SliceManifest(
            "feat-001", "Calculator",
            ["src/Features/calc", "src/Features/calc.Tests", "src/Features/calc.Resources"],
            [], "dotnet test",
            "src/Core", "");
        Write("src/Core/CalcHelper.cs", "namespace Core; public sealed class CalcHelper { }");
        Write("src/Features/calc.Resources/CalcHelper.cs", "namespace F; public sealed class CalcHelper { }");

        var violations = CoreReuseChecker.FindViolations(_workspace, manifest);

        Assert.Contains(violations, v => v.Contains("CalcHelper.cs"));
    }
}
