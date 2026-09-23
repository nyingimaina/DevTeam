using DevTeam.Broker.Context;

namespace DevTeam.Tests.Context;

public class RepoContextServiceTests : IDisposable
{
    private const string Commit1 = "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";
    private const string Commit2 = "b1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0";

    private readonly TempWorkspace _workspace = new();
    private readonly FakeRepomixRunner _repomix = new();
    private readonly FakeGitProbe _git = new();

    public void Dispose() => _workspace.Dispose();

    private RepoContextService CreateService(Action<RepoContextOptions>? configure = null)
    {
        var options = new RepoContextOptions();
        configure?.Invoke(options);
        return new RepoContextService(options, new RepoContextQueue(), _repomix, _git);
    }

    private string CurrentDir()
    {
        var index = RepoContextStore.ReadCurrent(_workspace.Path);
        Assert.NotNull(index);
        return index!.Dir;
    }

    private bool HasTempFolder()
        => Directory.Exists(RepoContextStore.ContextRoot(_workspace.Path)) &&
           Directory.EnumerateDirectories(RepoContextStore.ContextRoot(_workspace.Path))
               .Any(dir => Path.GetFileName(dir).StartsWith(".tmp-", StringComparison.Ordinal));

    [Fact]
    public async Task Refresh_WritesTheIndexAndPointsAtIt()
    {
        var service = CreateService();

        var result = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.Equal("refreshed", result.Outcome);
        Assert.Equal(Commit1, result.Commit);

        var dir = CurrentDir();
        foreach (var file in new[] { "pack.xml", "pack.compressed.xml", "structure.md", "CODEBASE_MAP.md", "meta.json" })
            Assert.True(_workspace.Exists($"devteam/context/{dir}/{file}"), $"missing {file}");

        var meta = RepoContextStore.ReadMeta(_workspace.Path, dir);
        Assert.NotNull(meta);
        Assert.Equal(Commit1, meta!.Commit);
        Assert.False(meta.Dirty);

        Assert.False(HasTempFolder(), "the temp build folder must be moved or removed");
        Assert.Contains("devteam/context/", File.ReadAllText(Path.Combine(_workspace.Path, ".git", "info", "exclude")));
    }

    [Fact]
    public async Task Refresh_IsIdempotentAtTheSameCommit()
    {
        var service = CreateService();
        await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);
        var callsAfterFirst = _repomix.Calls.Count;

        var second = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.Equal("up-to-date", second.Outcome);
        Assert.Equal(callsAfterFirst, _repomix.Calls.Count);
    }

    [Fact]
    public async Task Refresh_KeepsTheOldIndexWhenRepomixFails()
    {
        var service = CreateService();
        await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);
        var goodDir = CurrentDir();

        _git.Describe = new GitProbeResult(true, Commit2, "main", false);
        _repomix.Handler = (_, output, _) => new RepomixResult(RepomixOutcome.Missing, output, 0, string.Empty, "'repomix' is not recognized");

        var result = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.Equal("skipped", result.Outcome);
        Assert.Contains("repomix-missing", result.Warnings);
        Assert.Equal(goodDir, CurrentDir());
        Assert.False(Directory.Exists(RepoContextStore.CommitDir(_workspace.Path, RepoContextStore.CommitDirName(Commit2))));
        Assert.False(HasTempFolder());
    }

    [Fact]
    public async Task Refresh_DiscardsWhenHeadChangesMidRun()
    {
        var service = CreateService();
        _git.DescribeResults.Enqueue(new GitProbeResult(true, Commit1, "main", false));
        _git.DescribeResults.Enqueue(new GitProbeResult(true, Commit2, "release/other", false));

        var result = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.Equal("skipped", result.Outcome);
        Assert.Contains("head-changed", result.Warnings);
        Assert.Null(RepoContextStore.ReadCurrent(_workspace.Path));
        Assert.False(HasTempFolder());
    }

    [Fact]
    public async Task Refresh_DiscardsAnOversizedPack()
    {
        var service = CreateService(options => options.MaxPackBytes = 1);

        var result = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.Equal("skipped", result.Outcome);
        Assert.Contains("pack-too-large", result.Warnings);
        Assert.Null(RepoContextStore.ReadCurrent(_workspace.Path));
    }

    [Fact]
    public async Task Refresh_PrunesOldVersionsButNeverTheCurrentOne()
    {
        var service = CreateService(options => options.KeepLast = 2);

        var commits = new[] { "1111111111aa", "2222222222bb", "3333333333cc", "4444444444dd" };
        foreach (var commit in commits)
        {
            _git.Describe = new GitProbeResult(true, commit, "main", false);
            await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);
        }

        var folders = Directory.EnumerateDirectories(RepoContextStore.ContextRoot(_workspace.Path))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(2, folders.Count);
        Assert.Contains(CurrentDir(), folders);
    }

    [Fact]
    public async Task Exclude_IsIdempotent()
    {
        var first = RepoContextStore.EnsureGitExcluded(_workspace.Path);
        var second = RepoContextStore.EnsureGitExcluded(_workspace.Path);

        Assert.True(first);
        Assert.False(second);
        var lines = File.ReadAllLines(Path.Combine(_workspace.Path, ".git", "info", "exclude"))
            .Count(line => line.Trim() == "devteam/context/");
        Assert.Equal(1, lines);
    }

    [Fact]
    public async Task Disabled_IsANoOp()
    {
        var service = CreateService(options => options.Enabled = false);

        service.EnqueueRefresh(_workspace.Path, "feature-complete");
        var result = await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);

        Assert.False(service.Enabled);
        Assert.Equal("disabled", result.Outcome);
        Assert.Empty(_repomix.Calls);
        Assert.Null(RepoContextStore.ReadCurrent(_workspace.Path));
    }

    [Fact]
    public async Task Status_ReportsNoneUpToDateAndBehind()
    {
        var service = CreateService();

        var none = await service.GetStatusAsync(_workspace.Path, CancellationToken.None);
        Assert.Equal(RepoContextState.None, none.State);

        await service.RefreshAsync(_workspace.Path, "feature-complete", CancellationToken.None);
        var upToDate = await service.GetStatusAsync(_workspace.Path, CancellationToken.None);
        Assert.Equal(RepoContextState.UpToDate, upToDate.State);

        _git.Describe = new GitProbeResult(true, Commit2, "main", false);
        _git.Count = 4;
        var behind = await service.GetStatusAsync(_workspace.Path, CancellationToken.None);
        Assert.Equal(RepoContextState.Behind, behind.State);
        Assert.Equal(4, behind.ChangesBehind);
    }

    [Fact]
    public void Options_IgnoreListExcludesTheIndexAndLooksLikeSecrets()
    {
        var argument = new RepoContextOptions().BuildIgnoreArgument();

        Assert.Contains("devteam/context/**", argument);
        Assert.Contains("**/.env*", argument);
        Assert.Contains("**/*.pem", argument);
        Assert.Contains("**/*.key", argument);
    }
}
