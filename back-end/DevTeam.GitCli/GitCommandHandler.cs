namespace DevTeam.GitCli;

public class GitCommandHandler
{
    public async Task<GitResponse> HandleAsync(GitRequest request) =>
        request.Command.ToLowerInvariant() switch
        {
            "init" => await InitAsync(request),
            "status" => await StatusAsync(request),
            "branch" => await BranchAsync(request),
            "checkout" => await CheckoutAsync(request),
            "commit" => await CommitAsync(request),
            "merge" => await MergeAsync(request),
            "log" => await LogAsync(request),
            "ensure-branch" => await EnsureBranchAsync(request),
            "push" => await PushAsync(request),
            "delete-branch" => await DeleteBranchAsync(request),
            "has-remote" => await HasRemoteAsync(request),
            "get-remote" => await GetRemoteAsync(request),
            "set-remote" => await SetRemoteAsync(request),
            "stash-push" => await StashPushAsync(request),
            "stash-list" => await StashListAsync(request),
            "stash-apply" => await StashApplyAsync(request),
            "stash-drop" => await StashDropAsync(request),
            _ => new GitResponse(false, $"Unknown command: {request.Command}"),
        };

    private static async Task<GitResponse> InitAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        if (!Directory.Exists(request.WorkspacePath))
            return new GitResponse(false, $"Directory does not exist: {request.WorkspacePath}");

        var gitDir = Path.Combine(request.WorkspacePath, ".git");
        var isRepo = Directory.Exists(gitDir);

        if (!isRepo)
        {
            var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "init");
            if (exit != 0) return new GitResponse(false, $"git init failed: {err}");

            var (exit2, _, err2) = await RunGitAsync(request.WorkspacePath, "checkout", "-b", "main");
            if (exit2 != 0) await RunGitAsync(request.WorkspacePath, "branch", "-m", "master", "main");

            var (exit3, _, err3) = await RunGitAsync(request.WorkspacePath, "commit", "--allow-empty", "-m", "Initial commit");
            if (exit3 != 0) return new GitResponse(false, $"Initial commit failed: {err3}");
        }

        var (__, _, ___) = await RunGitAsync(request.WorkspacePath, "checkout", "-b", "develop");
        await RunGitAsync(request.WorkspacePath, "checkout", "develop");

        var statusResult = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, isRepo ? "Repository initialized" : "Repository already exists",
            IsRepo: true, Branch: statusResult.branch, IsClean: statusResult.isClean);
    }

    private static async Task<GitResponse> StatusAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        var gitDir = Path.Combine(request.WorkspacePath, ".git");
        if (!Directory.Exists(gitDir))
            return new GitResponse(false, "Not a git repository", IsRepo: false);

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, "OK",
            IsRepo: true, Branch: status.branch, IsClean: status.isClean,
            Ahead: status.ahead, Behind: status.behind, ChangedFiles: status.changedFiles);
    }

    private static async Task<GitResponse> BranchAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.BranchName is null)
            return new GitResponse(false, "branchName required");

        var (exit, out_, err) = await RunGitAsync(request.WorkspacePath, "branch", request.BranchName);
        if (exit != 0) return new GitResponse(false, $"git branch failed: {err}");

        var branches = await ListBranchesAsync(request.WorkspacePath);
        return new GitResponse(true, $"Branch '{request.BranchName}' created", Branches: branches);
    }

    private static async Task<GitResponse> CheckoutAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.BranchName is null)
            return new GitResponse(false, "branchName required");

        var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "checkout", request.BranchName);
        if (exit != 0) return new GitResponse(false, $"git checkout failed: {err}");

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, $"Checked out '{request.BranchName}'",
            Branch: status.branch, IsClean: status.isClean);
    }

    private static async Task<GitResponse> CommitAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.Message is null)
            return new GitResponse(false, "message required");

        var (exit1, _, err1) = await RunGitAsync(request.WorkspacePath, "add", "-A");
        if (exit1 != 0) return new GitResponse(false, $"git add failed: {err1}");

        var (exit2, out2, err2) = await RunGitAsync(request.WorkspacePath, "commit", "-m", request.Message);
        if (exit2 != 0)
        {
            if (out2.Contains("nothing to commit"))
                return new GitResponse(true, "Nothing to commit");
            return new GitResponse(false, $"git commit failed: {err2}");
        }

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, "Committed", Branch: status.branch, IsClean: status.isClean);
    }

    private static async Task<GitResponse> MergeAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.SourceBranch is null)
            return new GitResponse(false, "sourceBranch required");

        var (exit1, _, err1) = await RunGitAsync(request.WorkspacePath, "checkout", request.SourceBranch);
        if (exit1 != 0) return new GitResponse(false, $"checkout {request.SourceBranch} failed: {err1}");

        var target = request.TargetBranch ?? "develop";
        var (exit2, out2, err2) = await RunGitAsync(request.WorkspacePath, "checkout", target);
        if (exit2 != 0) return new GitResponse(false, $"checkout {target} failed: {err2}");

        var (exit3, out3, err3) = await RunGitAsync(request.WorkspacePath, "merge", request.SourceBranch, "--no-ff",
            "-m", $"Merge '{request.SourceBranch}' into {target}");
        if (exit3 != 0)
        {
            await RunGitAsync(request.WorkspacePath, "merge", "--abort");
            return new GitResponse(false, $"merge failed: {err3}");
        }

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, $"Merged '{request.SourceBranch}' into '{target}'",
            Branch: status.branch, IsClean: status.isClean);
    }

    private static async Task<GitResponse> LogAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        var (exit, out_, err) = await RunGitAsync(request.WorkspacePath, "log",
            "--format=%H|%h|%s|%an|%aI|%P", "-30");
        if (exit != 0) return new GitResponse(false, $"git log failed: {err}");

        var tags = await GetTagsAsync(request.WorkspacePath);
        var branches = await ListBranchesAsync(request.WorkspacePath);
        var currentBranch = (await RunGitAsync(request.WorkspacePath, "rev-parse", "--abbrev-ref", "HEAD")).output.Trim();

        var commits = out_.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                if (parts.Length < 6) return null;
                var hash = parts[0];
                var parents = parts[5].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var commitTags = tags.Where(t => t.Hash == hash).Select(t => t.Name).ToArray();
                var commitBranch = branches.FirstOrDefault(b => {
                    var (_, bHash, _) = RunGitAsync(request.WorkspacePath, "rev-parse", b).Result;
                    return bHash.Trim() == hash;
                });
                return new GitCommit(
                    Hash: hash,
                    ShortHash: parts[1],
                    Message: parts[2],
                    Author: parts[3],
                    Date: parts[4],
                    Parents: parents,
                    Branch: commitBranch ?? (hash == "HEAD" ? currentBranch : null),
                    Tags: commitTags.Length > 0 ? commitTags : null);
            })
            .Where(c => c is not null)
            .Cast<GitCommit>()
            .ToArray();

        return new GitResponse(true, "OK", Commits: commits);
    }

    private static async Task<(string Name, string Hash)[]> GetTagsAsync(string workspacePath)
    {
        var (_, out_, _) = await RunGitAsync(workspacePath, "tag", "--format=%(refname:short)|%(objectname)");
        return out_.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line =>
            {
                var parts = line.Split('|');
                return parts.Length == 2 ? (parts[0], parts[1]) : ("", "");
            })
            .Where(t => !string.IsNullOrEmpty(t.Item1))
            .ToArray();
    }

    private static async Task<GitResponse> EnsureBranchAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.BranchName is null)
            return new GitResponse(false, "branchName required");

        var branches = await ListBranchesAsync(request.WorkspacePath);
        var exists = branches.Any(b => b == request.BranchName);

        if (!exists)
        {
            var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "branch", request.BranchName);
            if (exit != 0) return new GitResponse(false, $"create branch failed: {err}");
        }

        var (exit2, _, err2) = await RunGitAsync(request.WorkspacePath, "checkout", request.BranchName);
        if (exit2 != 0) return new GitResponse(false, $"checkout failed: {err2}");

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, exists ? $"Checked out '{request.BranchName}'" : $"Created and checked out '{request.BranchName}'",
            Branch: status.branch, IsClean: status.isClean);
    }

    private static async Task<GitResponse> PushAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.BranchName is null)
            return new GitResponse(false, "branchName required");

        var (hasRemote, _) = await GetRemoteUrlAsync(request.WorkspacePath);
        if (!hasRemote)
            return new GitResponse(false, "No remote configured");

        var authArgs = BuildAuthArgs(request.AuthToken);
        var (exit, _, err) = await RunGitAsync(request.WorkspacePath,
            [.. authArgs, "push", "-u", "origin", request.BranchName]);
        if (exit != 0) return new GitResponse(false, $"git push failed: {err}");

        return new GitResponse(true, $"Pushed '{request.BranchName}' to origin");
    }

    private static async Task<GitResponse> DeleteBranchAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.BranchName is null)
            return new GitResponse(false, "branchName required");

        var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "branch", "-d", request.BranchName);
        if (exit != 0) return new GitResponse(false, $"git branch -d failed: {err}");

        var (hasRemote, _) = await GetRemoteUrlAsync(request.WorkspacePath);
        if (hasRemote)
        {
            var authArgs = BuildAuthArgs(request.AuthToken);
            var (remoteExit, _, remoteErr) = await RunGitAsync(request.WorkspacePath,
                [.. authArgs, "push", "origin", "--delete", request.BranchName]);
            if (remoteExit != 0)
                return new GitResponse(true, $"Deleted local branch '{request.BranchName}'; remote deletion failed: {remoteErr}");
        }

        return new GitResponse(true, $"Deleted branch '{request.BranchName}'");
    }

    private static async Task<GitResponse> HasRemoteAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        var (hasRemote, url) = await GetRemoteUrlAsync(request.WorkspacePath);
        return new GitResponse(true, "OK", HasRemote: hasRemote, RemoteUrl: url);
    }

    private static async Task<GitResponse> GetRemoteAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        var (hasRemote, url) = await GetRemoteUrlAsync(request.WorkspacePath);
        return new GitResponse(true, "OK", HasRemote: hasRemote, RemoteUrl: url);
    }

    private static async Task<GitResponse> SetRemoteAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (string.IsNullOrWhiteSpace(request.RemoteUrl))
            return new GitResponse(false, "remoteUrl required");

        var (hasRemote, _) = await GetRemoteUrlAsync(request.WorkspacePath);
        var (exit, _, err) = hasRemote
            ? await RunGitAsync(request.WorkspacePath, "remote", "set-url", "origin", request.RemoteUrl)
            : await RunGitAsync(request.WorkspacePath, "remote", "add", "origin", request.RemoteUrl);
        if (exit != 0) return new GitResponse(false, $"git remote failed: {err}");

        return new GitResponse(true, "Remote set", HasRemote: true, RemoteUrl: request.RemoteUrl);
    }

    private static async Task<GitResponse> StashPushAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.Message is null)
            return new GitResponse(false, "message (stash tag) required");

        // -u includes untracked files — an agent's newly-scaffolded files shouldn't be
        // silently dropped when parking a feature's WIP.
        var (exit, out_, err) = await RunGitAsync(request.WorkspacePath, "stash", "push", "-u", "-m", request.Message);
        if (exit != 0) return new GitResponse(false, $"git stash push failed: {err}");
        if (out_.Contains("No local changes to save"))
            return new GitResponse(true, "No local changes to stash");

        return new GitResponse(true, $"Stashed as '{request.Message}'");
    }

    private static async Task<GitResponse> StashListAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");

        var (exit, out_, err) = await RunGitAsync(request.WorkspacePath, "stash", "list");
        if (exit != 0) return new GitResponse(false, $"git stash list failed: {err}");

        var entries = out_.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new GitResponse(true, "OK", StashEntries: entries);
    }

    private static async Task<GitResponse> StashApplyAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.Message is null)
            return new GitResponse(false, "message (stash tag) required");

        var (found, stashRef) = await FindStashRefAsync(request.WorkspacePath, request.Message);
        if (!found) return new GitResponse(false, $"No stash found tagged '{request.Message}'");

        // Deliberately "apply", not "pop": pop auto-drops the entry even when the apply
        // conflicts, which would lose the parked work. The caller drops it explicitly (via
        // stash-drop) only once it has confirmed the apply fully succeeded.
        var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "stash", "apply", stashRef!);
        if (exit != 0) return new GitResponse(false, $"git stash apply failed: {err}");

        var status = await GetStatusAsync(request.WorkspacePath);
        return new GitResponse(true, $"Applied stash '{request.Message}'", Branch: status.branch, IsClean: status.isClean);
    }

    private static async Task<GitResponse> StashDropAsync(GitRequest request)
    {
        if (request.WorkspacePath is null)
            return new GitResponse(false, "workspacePath required");
        if (request.Message is null)
            return new GitResponse(false, "message (stash tag) required");

        var (found, stashRef) = await FindStashRefAsync(request.WorkspacePath, request.Message);
        if (!found) return new GitResponse(false, $"No stash found tagged '{request.Message}'");

        var (exit, _, err) = await RunGitAsync(request.WorkspacePath, "stash", "drop", stashRef!);
        if (exit != 0) return new GitResponse(false, $"git stash drop failed: {err}");

        return new GitResponse(true, $"Dropped stash '{request.Message}'");
    }

    // Resolves a caller-supplied tag to its current stash@{n} ref — a stash's position in the
    // stack shifts as other entries are pushed/dropped, so callers must always look it up by
    // tag rather than assuming a fixed index.
    private static async Task<(bool Found, string? StashRef)> FindStashRefAsync(string workspacePath, string tag)
    {
        var (exit, out_, _) = await RunGitAsync(workspacePath, "stash", "list");
        if (exit != 0) return (false, null);

        foreach (var line in out_.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains(tag, StringComparison.Ordinal)) continue;
            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0) continue;
            return (true, line[..colonIndex].Trim());
        }
        return (false, null);
    }

    private static async Task<(bool hasRemote, string? url)> GetRemoteUrlAsync(string workspacePath)
    {
        var (exit, out_, _) = await RunGitAsync(workspacePath, "remote", "get-url", "origin");
        if (exit != 0) return (false, null);
        var url = out_.Trim();
        return (!string.IsNullOrEmpty(url), string.IsNullOrEmpty(url) ? null : url);
    }

    private static string[] BuildAuthArgs(string? authToken)
    {
        if (string.IsNullOrEmpty(authToken)) return [];
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"x-access-token:{authToken}"));
        return ["-c", $"http.extraHeader=AUTHORIZATION: basic {encoded}"];
    }

    private static async Task<(string branch, bool isClean, int ahead, int behind, string[] changedFiles)> GetStatusAsync(string workspacePath)
    {
        var (_, branchOut, _) = await RunGitAsync(workspacePath, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = branchOut.Trim();

        var (exitCode, statusOut, _) = await RunGitAsync(workspacePath, "status", "--porcelain", "--untracked-files=all");
        var isClean = string.IsNullOrWhiteSpace(statusOut);
        var changedFiles = ParsePorcelainPaths(statusOut);

        var (__, aheadOut, _) = await RunGitAsync(workspacePath, "rev-list", "--count", "--left-right", $"@{{upstream}}...HEAD");
        int ahead = 0, behind = 0;
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(aheadOut))
        {
            var parts = aheadOut.Trim().Split('\t');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out behind);
                int.TryParse(parts[1], out ahead);
            }
        }

        return (branch, isClean, ahead, behind, changedFiles);
    }

    // Each porcelain line is "XY path" or, for a rename, "XY oldPath -> newPath" — take the
    // path itself (git always uses forward slashes in porcelain output, even on Windows).
    private static string[] ParsePorcelainPaths(string statusOut)
    {
        if (string.IsNullOrWhiteSpace(statusOut)) return [];

        var paths = new List<string>();
        foreach (var line in statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 4) continue;
            var pathPart = line[3..].Trim();
            var arrowIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
            paths.Add(arrowIndex >= 0 ? pathPart[(arrowIndex + 4)..] : pathPart);
        }
        return [.. paths];
    }

    private static async Task<string[]> ListBranchesAsync(string workspacePath)
    {
        var (_, out_, _) = await RunGitAsync(workspacePath, "branch", "--format=%(refname:short)");
        return out_.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task<(int exitCode, string output, string error)> RunGitAsync(string workingDir, params string[] args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", ex.Message);
        }
    }
}
