using DevTeam.Broker.Context;
using DevTeam.Broker.Gates;
using DevTeam.Broker.Workflow;

using DevTeam.Tests.Context;

namespace DevTeam.Tests.Gates;

public class CodeMapGateTests : IDisposable
{
    private const string Commit = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";

    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    private sealed class StubRepoContextService(bool enabled = true) : IRepoContextService
    {
        public bool Enabled { get; } = enabled;

        public void EnqueueRefresh(string workspacePath, string trigger)
        {
        }

        public Task<RepoContextStatus> GetStatusAsync(string workspacePath, CancellationToken ct)
            => Task.FromResult(new RepoContextStatus(RepoContextState.UpToDate, 0, DateTimeOffset.UtcNow, []));

        public Task<RepoContextRefreshResult> RefreshAsync(string workspacePath, string trigger, CancellationToken ct)
            => Task.FromResult(RepoContextRefreshResult.Skipped("stub"));
    }

    private void SeedIndex(string map = "# Codebase map\n\n## Modules\n- core", string? compressedPack = null)
    {
        var dir = RepoContextStore.CommitDirName(Commit);
        RepoContextStore.WriteCurrent(_workspace.Path, new RepoContextIndex(Commit, dir, DateTimeOffset.UtcNow));
        RepoContextStore.WriteMeta(
            RepoContextStore.CommitDir(_workspace.Path, dir),
            new RepoContextMeta { Commit = Commit, BuiltAtUtc = DateTimeOffset.UtcNow });
        _workspace.Write($"devteam/context/{dir}/CODEBASE_MAP.md", map);
        if (compressedPack is not null)
            _workspace.Write($"devteam/context/{dir}/pack.compressed.xml", compressedPack);
    }

    private Task<GateResult> RunAsync() =>
        new CodeMapGate().RunAsync(
            new GateRequest(BuiltinRegistry.CodeMap, _workspace.Path, "feat-001", "developer"),
            CancellationToken.None);

    [Fact]
    public async Task NoIndex_PassesWithoutWriting()
    {
        var result = await RunAsync();

        Assert.True(result.Passed);
        Assert.False(_workspace.Exists("devteam/features/feat-001/codemap.md"));
    }

    [Fact]
    public async Task WithIndex_WritesTheMapIntoTheFeatureFolder()
    {
        SeedIndex("# Codebase map\n\n## Modules\n- `back-end/src` — 3 file(s)");

        var result = await RunAsync();

        Assert.True(result.Passed);
        var codemap = File.ReadAllText(Path.Combine(_workspace.Path, "devteam", "features", "feat-001", "codemap.md"));
        Assert.Contains("Project overview", codemap);
        Assert.Contains("`back-end/src`", codemap);
    }

    [Fact]
    public async Task Disabled_PassesWithoutWriting()
    {
        SeedIndex();
        var gate = new CodeMapGate(new StubRepoContextService(enabled: false));

        var result = await gate.RunAsync(
            new GateRequest(BuiltinRegistry.CodeMap, _workspace.Path, "feat-001", "developer"),
            CancellationToken.None);

        Assert.True(result.Passed);
        Assert.False(_workspace.Exists("devteam/features/feat-001/codemap.md"));
    }

    [Fact]
    public async Task Slice_ContainsOnlyTheFeatureSlice()
    {
        SeedIndex(compressedPack:
            "<files>\n" +
            "<file path=\"back-end/src/Features/feat-001/Handler.cs\">allowed</file>\n" +
            "<file path=\"back-end/src/Features/other/Other.cs\">not allowed</file>\n" +
            "<file path=\"front-end/app/feat-001/page.tsx\">allowed front</file>\n" +
            "</files>");
        SliceManifestIO.Write(
            ArtifactPaths.ManifestPath(_workspace.Path, "feat-001"),
            new SliceManifest("feat-001", "Login", "back-end/src/Features/<F>", "front-end/app/<F>", [], "dotnet test"));

        var result = await RunAsync();

        Assert.True(result.Passed);
        var slice = File.ReadAllText(Path.Combine(_workspace.Path, "devteam", "features", "feat-001", "codeslice.xml"));
        Assert.Contains("Handler.cs", slice);
        Assert.Contains("page.tsx", slice);
        Assert.DoesNotContain("Other.cs", slice);
    }
}
