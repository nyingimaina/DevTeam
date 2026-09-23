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
    public async Task RevParse_ReturnsTheHeadCommitHash()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));

        var result = await _handler.HandleAsync(new GitRequest("rev-parse", _tempDir, BranchName: "HEAD"));

        Assert.True(result.Success);
        Assert.Matches("^[0-9a-f]{40}$", result.CommitSha!);
    }

    [Fact]
    public async Task RevParse_UnknownRef_Fails()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));

        var result = await _handler.HandleAsync(new GitRequest("rev-parse", _tempDir, BranchName: "no-such-ref"));

        Assert.False(result.Success);
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
    public async Task Status_ReportsRelativePathsOfCreatedAndModifiedFiles()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "committed.txt"), "v1");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "initial"));

        await File.WriteAllTextAsync(Path.Combine(_tempDir, "committed.txt"), "v2");
        Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "sub", "new.txt"), "new");

        var result = await _handler.HandleAsync(new GitRequest("status", _tempDir));

        Assert.True(result.Success);
        Assert.NotNull(result.ChangedFiles);
        Assert.Contains("committed.txt", result.ChangedFiles!);
        Assert.Contains("sub/new.txt", result.ChangedFiles!);
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
    public async Task Merge_WithConflict_LeavesWorkingTreeConflictedAndReportsConflictedFiles()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var filePath = Path.Combine(_tempDir, "shared.txt");
        await File.WriteAllTextAsync(filePath, "base\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "base"));

        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/conflict"));
        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "feature/conflict"));
        await File.WriteAllTextAsync(filePath, "feature change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "feature edit"));

        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "develop"));
        await File.WriteAllTextAsync(filePath, "develop change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "develop edit"));

        var result = await _handler.HandleAsync(new GitRequest("merge", _tempDir, SourceBranch: "feature/conflict", TargetBranch: "develop"));

        Assert.False(result.Success);
        Assert.NotNull(result.ConflictedFiles);
        Assert.Contains("shared.txt", result.ConflictedFiles!);
        // Left in its natural conflicted state — not auto-aborted — so a resolution step
        // (human or LLM) can actually see and fix what conflicted.
        Assert.True(File.Exists(Path.Combine(_tempDir, ".git", "MERGE_HEAD")));
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("<<<<<<<", content);
    }

    [Fact]
    public async Task MergeAbort_AfterConflict_RestoresACleanWorkingTree()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var filePath = Path.Combine(_tempDir, "shared.txt");
        await File.WriteAllTextAsync(filePath, "base\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "base"));

        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/conflict"));
        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "feature/conflict"));
        await File.WriteAllTextAsync(filePath, "feature change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "feature edit"));

        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "develop"));
        await File.WriteAllTextAsync(filePath, "develop change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "develop edit"));

        await _handler.HandleAsync(new GitRequest("merge", _tempDir, SourceBranch: "feature/conflict", TargetBranch: "develop"));

        var result = await _handler.HandleAsync(new GitRequest("merge-abort", _tempDir));

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(_tempDir, ".git", "MERGE_HEAD")));
        var status = await _handler.HandleAsync(new GitRequest("status", _tempDir));
        Assert.True(status.IsClean);
    }

    [Fact]
    public async Task StashApply_WithConflict_LeavesConflictMarkersAndReportsConflictedFiles()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var filePath = Path.Combine(_tempDir, "shared.txt");
        await File.WriteAllTextAsync(filePath, "base\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "base"));

        await File.WriteAllTextAsync(filePath, "stashed change\n");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "tag-a"));

        await File.WriteAllTextAsync(filePath, "committed change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "committed edit"));

        var result = await _handler.HandleAsync(new GitRequest("stash-apply", _tempDir, Message: "tag-a"));

        Assert.False(result.Success);
        Assert.NotNull(result.ConflictedFiles);
        Assert.Contains("shared.txt", result.ConflictedFiles!);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("<<<<<<<", content);
    }

    [Fact]
    public async Task StageAll_AfterResolvingConflictMarkersByHand_ClearsTheConflictedFilesList()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var filePath = Path.Combine(_tempDir, "shared.txt");
        await File.WriteAllTextAsync(filePath, "base\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "base"));

        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/conflict"));
        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "feature/conflict"));
        await File.WriteAllTextAsync(filePath, "feature change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "feature edit"));

        await _handler.HandleAsync(new GitRequest("checkout", _tempDir, BranchName: "develop"));
        await File.WriteAllTextAsync(filePath, "develop change\n");
        await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "develop edit"));

        var mergeResult = await _handler.HandleAsync(new GitRequest("merge", _tempDir, SourceBranch: "feature/conflict", TargetBranch: "develop"));
        Assert.False(mergeResult.Success);
        Assert.Contains("shared.txt", mergeResult.ConflictedFiles!);

        // Simulates an agent resolving the conflict by editing the file's content directly —
        // stage-all (git add -A) is the deterministic step that tells git the conflict is
        // resolved, mirroring what ResolveConflictAsync does after an LLM turn.
        await File.WriteAllTextAsync(filePath, "resolved content\n");
        var stageResult = await _handler.HandleAsync(new GitRequest("stage-all", _tempDir));

        Assert.True(stageResult.Success);
        Assert.NotNull(stageResult.ConflictedFiles);
        Assert.Empty(stageResult.ConflictedFiles!);

        // The merge is now completable with a normal commit.
        var commitResult = await _handler.HandleAsync(new GitRequest("commit", _tempDir, Message: "merge resolved"));
        Assert.True(commitResult.Success);
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
    public async Task StashPush_WithDirtyWorkingTree_StashesChangesAndCleansTheTree()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "wip.txt"), "wip");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "untracked.txt"), "new file");

        var result = await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-abc"));

        Assert.True(result.Success);
        var status = await _handler.HandleAsync(new GitRequest("status", _tempDir));
        Assert.True(status.IsClean);
        // -u means untracked files are stashed too, not just tracked ones.
        Assert.False(File.Exists(Path.Combine(_tempDir, "wip.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "untracked.txt")));
    }

    [Fact]
    public async Task StashPush_WithNothingToStash_StillReportsSuccess()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));

        var result = await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-abc"));

        Assert.True(result.Success);
    }

    [Fact]
    public async Task StashList_ReturnsEntriesTaggedByMessage()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.txt"), "a");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-one"));

        var result = await _handler.HandleAsync(new GitRequest("stash-list", _tempDir));

        Assert.True(result.Success);
        Assert.NotNull(result.StashEntries);
        Assert.Single(result.StashEntries!);
        Assert.Contains("devteam-feature-one", result.StashEntries![0]);
    }

    [Fact]
    public async Task StashApply_FindsTheRightEntryByTagRegardlessOfStackPosition()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        // Push two stashes so the target one is NOT at the top of the stack.
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "older.txt"), "older");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-older"));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "newer.txt"), "newer");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-newer"));

        var result = await _handler.HandleAsync(new GitRequest("stash-apply", _tempDir, Message: "devteam-feature-older"));

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(_tempDir, "older.txt")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "newer.txt")));
    }

    [Fact]
    public async Task StashApply_WithNoMatchingTag_ReturnsFailureInsteadOfApplyingTheWrongOne()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.txt"), "a");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-one"));

        var result = await _handler.HandleAsync(new GitRequest("stash-apply", _tempDir, Message: "devteam-feature-does-not-exist"));

        Assert.False(result.Success);
        Assert.False(File.Exists(Path.Combine(_tempDir, "a.txt")));
    }

    [Fact]
    public async Task StashDrop_RemovesTheEntry()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.txt"), "a");
        await _handler.HandleAsync(new GitRequest("stash-push", _tempDir, Message: "devteam-feature-one"));

        var dropResult = await _handler.HandleAsync(new GitRequest("stash-drop", _tempDir, Message: "devteam-feature-one"));
        Assert.True(dropResult.Success);

        var listResult = await _handler.HandleAsync(new GitRequest("stash-list", _tempDir));
        Assert.Empty(listResult.StashEntries ?? []);
    }

    [Fact]
    public async Task HasRemote_NoRemoteConfigured_ReturnsFalse()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("has-remote", _tempDir));

        Assert.True(result.Success);
        Assert.False(result.HasRemote);
    }

    [Fact]
    public async Task GetRemote_NoRemoteConfigured_ReturnsNullUrl()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("get-remote", _tempDir));

        Assert.True(result.Success);
        Assert.Null(result.RemoteUrl);
    }

    [Fact]
    public async Task SetRemote_ThenGetRemote_RoundTrips()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var bareDir = Path.Combine(Path.GetTempPath(), "gitcli-bare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bareDir);
        try
        {
            var setResult = await _handler.HandleAsync(new GitRequest("set-remote", _tempDir, RemoteUrl: bareDir));
            Assert.True(setResult.Success);

            var hasRemote = await _handler.HandleAsync(new GitRequest("has-remote", _tempDir));
            Assert.True(hasRemote.HasRemote);

            var getResult = await _handler.HandleAsync(new GitRequest("get-remote", _tempDir));
            Assert.Equal(bareDir, getResult.RemoteUrl);

            // Setting again should update, not fail, an existing remote.
            var otherDir = Path.Combine(Path.GetTempPath(), "gitcli-bare2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(otherDir);
            try
            {
                var updateResult = await _handler.HandleAsync(new GitRequest("set-remote", _tempDir, RemoteUrl: otherDir));
                Assert.True(updateResult.Success);
                var reGet = await _handler.HandleAsync(new GitRequest("get-remote", _tempDir));
                Assert.Equal(otherDir, reGet.RemoteUrl);
            }
            finally
            {
                Directory.Delete(otherDir, recursive: true);
            }
        }
        finally
        {
            TryDeleteDirectory(bareDir);
        }
    }

    [Fact]
    public async Task Push_NoRemoteConfigured_ReturnsFailureWithoutThrowing()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var result = await _handler.HandleAsync(new GitRequest("push", _tempDir, BranchName: "develop"));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task Push_WithRemoteConfigured_PushesBranchToRemote()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var bareDir = Path.Combine(Path.GetTempPath(), "gitcli-bare-" + Guid.NewGuid().ToString("N"));
        RunGitRaw(bareDir, "init", "--bare");
        try
        {
            await _handler.HandleAsync(new GitRequest("set-remote", _tempDir, RemoteUrl: bareDir));
            var result = await _handler.HandleAsync(new GitRequest("push", _tempDir, BranchName: "develop"));

            Assert.True(result.Success);
        }
        finally
        {
            TryDeleteDirectory(bareDir);
        }
    }

    [Fact]
    public async Task DeleteBranch_LocalOnly_NoRemote_DeletesLocalBranch()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/gone"));

        var result = await _handler.HandleAsync(new GitRequest("delete-branch", _tempDir, BranchName: "feature/gone"));

        Assert.True(result.Success);
        var branchList = await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "throwaway-probe"));
        Assert.DoesNotContain("feature/gone", branchList.Branches!);
    }

    [Fact]
    public async Task DeleteBranch_WithRemote_DeletesLocalAndRemoteBranch()
    {
        await _handler.HandleAsync(new GitRequest("init", _tempDir));
        var bareDir = Path.Combine(Path.GetTempPath(), "gitcli-bare-" + Guid.NewGuid().ToString("N"));
        RunGitRaw(bareDir, "init", "--bare");
        try
        {
            await _handler.HandleAsync(new GitRequest("set-remote", _tempDir, RemoteUrl: bareDir));
            await _handler.HandleAsync(new GitRequest("branch", _tempDir, BranchName: "feature/gone"));
            await _handler.HandleAsync(new GitRequest("push", _tempDir, BranchName: "feature/gone"));

            var result = await _handler.HandleAsync(new GitRequest("delete-branch", _tempDir, BranchName: "feature/gone"));

            Assert.True(result.Success);
            var remoteBranches = RunGitRaw(_tempDir, "ls-remote", "--heads", "origin");
            Assert.DoesNotContain("feature/gone", remoteBranches);
        }
        finally
        {
            TryDeleteDirectory(bareDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                ClearReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch when (i < 4)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(200);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
    }

    private static string RunGitRaw(string workingDir, params string[] args)
    {
        Directory.CreateDirectory(workingDir);
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
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
