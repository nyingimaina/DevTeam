using DevTeam.GitCli;

namespace DevTeam.GitCli.Tests;

public class GitCommandHandlerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly GitCommandHandler _handler = new();

    public GitCommandHandlerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gitcli-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            // Git creates locked files on Windows; retry deletion
            for (var i = 0; i < 3; i++)
            {
                try
                {
                    Directory.Delete(_tempDir, recursive: true);
                    break;
                }
                catch when (i < 2)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    Thread.Sleep(100);
                }
            }
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Init_CreatesGitRepo()
    {
        var result = await _handler.HandleAsync(new GitRequest("init", _tempDir));

        Assert.True(result.Success);
        Assert.True(result.IsRepo);
        Assert.NotNull(result.Branch);
        Assert.True(Directory.Exists(Path.Combine(_tempDir, ".git")));
    }

    [Fact]
    public async Task Init_OnExistingRepo_ReturnsOk()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("init", _tempDir));

        Assert.True(result.Success);
        Assert.True(result.IsRepo);
    }

    [Fact]
    public async Task Status_OnRepo_ReturnsBranchInfo()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("status", _tempDir));

        Assert.True(result.Success);
        Assert.True(result.IsRepo);
        Assert.NotNull(result.Branch);
    }

    [Fact]
    public async Task Branch_CreatesNewBranch()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/test"));

        Assert.True(result.Success);
        Assert.NotNull(result.Branches);
        Assert.Contains("feature/test", result.Branches!);
    }

    [Fact]
    public async Task Checkout_SwitchesBranch()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/test"));
        var result = await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "feature/test"));

        Assert.True(result.Success);
        Assert.Equal("feature/test", result.Branch);
    }

    [Fact]
    public async Task Commit_CreatesCommit()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "test.txt"), "hello");
        var result = await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "test commit"));

        Assert.True(result.Success);
        Assert.True(result.IsClean);
    }

    [Fact]
    public async Task Commit_NothingToCommit_ReturnsSuccess()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "empty commit"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Merge_MergesBranch()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/test"));
        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "feature/test"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "feature.txt"), "feature");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "add feature"));

        var result = await _handler.HandleAsync(new GitRequest("merge", _tempDir, SourceBranch: "feature/test", TargetBranch: "develop"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task EnsureBranch_CreatesIfNotExists()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("ensure-branch", _tempDir, BranchName: "release/v1.0"));

        Assert.True(result.Success);
        Assert.Contains("Created", result.Message!);
    }

    [Fact]
    public async Task EnsureBranch_ChecksOutIfExists()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/test"));
        var result = await _handler.HandleAsync(new GitRequest("ensure-branch", _tempDir, BranchName: "feature/test"));

        Assert.True(result.Success);
        Assert.Contains("Checked out", result.Message!);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var result = await _handler.HandleAsync(new GitRequest("bogus", _tempDir));

        Assert.False(result.Success);
        Assert.Contains("Unknown command", result.Message);
    }

    [Fact]
    public async Task MissingWorkspacePath_ReturnsError()
    {
        var result = await _handler.HandleAsync(new GitRequest("init"));

        Assert.False(result.Success);
        Assert.Contains("required", result.Message!);
    }
}
