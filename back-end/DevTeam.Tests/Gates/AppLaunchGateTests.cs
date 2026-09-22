using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;
using DevTeam.Tests.Context;

namespace DevTeam.Tests.Gates;

public class AppLaunchGateTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private GateRequest Request(string featureKey = "feat-001")
        => new(BuiltinRegistry.AppLaunch, _workspace.Path, featureKey);

    private static FakeProcessRunner RunnerReturning(ProcessRunResult result)
        => new(_ => result);

    [Fact]
    public async Task PassesWhenThereIsNoExecutable()
    {
        var result = await new AppLaunchGate(RunnerReturning(new ProcessRunResult(0, "", "", false, TimeSpan.Zero)))
            .RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("no runnable app", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PassesWhenTheAppStaysOpen()
    {
        _workspace.Write("app/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        _workspace.Write("app/bin/Debug/net10.0-windows/App.exe", "binary");

        var result = await new AppLaunchGate(RunnerReturning(new ProcessRunResult(-1, "", "", true, TimeSpan.Zero)))
            .RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Contains("stayed open", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FailsWhenTheAppCrashesAtStartup()
    {
        _workspace.Write("app/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        _workspace.Write("app/bin/Debug/net10.0-windows/App.exe", "binary");

        var result = await new AppLaunchGate(
                RunnerReturning(new ProcessRunResult(unchecked((int)0xE0434352), "", "Cannot locate resource 'x'", false, TimeSpan.Zero)))
            .RunAsync(Request(), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("E0434352", result.EvidenceText);
        Assert.Contains("Cannot locate resource", result.EvidenceText);
    }

    [Fact]
    public async Task UsesTheManifestRunCommandWhenSet()
    {
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"),
            new SliceManifest("feat-001", "App", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test",
                runCommand: "my-app --smoke"));

        var runner = RunnerReturning(new ProcessRunResult(-1, "", "", true, TimeSpan.Zero));
        var result = await new AppLaunchGate(runner).RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("my-app", call.FileName);
        Assert.Equal("--smoke", call.Arguments);
    }

    [Fact]
    public async Task UsesTheWorkspaceAsWorkingDirectoryWhenTheManifestRunCommandIsABareExecutable()
    {
        // A manifest runCommand like "dotnet run --project back-end/src/App.csproj" resolves to
        // FileName="dotnet" (no directory component) — Path.GetDirectoryName("dotnet") returns ""
        // (not null), so a naive `?? workspacePath` fallback never triggers and the process would
        // launch with an empty working directory, breaking the project's relative --project path.
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"),
            new SliceManifest("feat-001", "App", "back-end/**/Features/<F>", "front-end/app/<F>", [], "dotnet test",
                runCommand: "dotnet run --project back-end/src/App.csproj"));

        var runner = RunnerReturning(new ProcessRunResult(0, "", "", false, TimeSpan.Zero));
        var result = await new AppLaunchGate(runner).RunAsync(Request(), CancellationToken.None);

        Assert.True(result.Passed);
        var call = Assert.Single(runner.Calls);
        Assert.Equal("dotnet", call.FileName);
        Assert.Equal(_workspace.Path, call.WorkingDirectory);
    }

    [Fact]
    public void FindAppExecutable_FindsTheBuiltExecutableOfAnExeProject()
    {
        _workspace.Write("back-end/src/Core/Calc.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType></PropertyGroup></Project>");
        _workspace.Write("back-end/src/Core/bin/Debug/net10.0-windows/Calc.exe", "binary");
        _workspace.Write("back-end/src/Core/Calc.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");

        var exe = AppLaunchGate.FindAppExecutable(_workspace.Path);

        Assert.NotNull(exe);
        Assert.EndsWith("Calc.exe", exe, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindAppExecutable_ReturnsNullForALibraryOnlyWorkspace()
    {
        _workspace.Write("back-end/src/Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        Assert.Null(AppLaunchGate.FindAppExecutable(_workspace.Path));
    }
}
