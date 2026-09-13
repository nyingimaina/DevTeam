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
            Ahead: status.ahead, Behind: status.behind);
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

    private static async Task<(string branch, bool isClean, int ahead, int behind)> GetStatusAsync(string workspacePath)
    {
        var (_, branchOut, _) = await RunGitAsync(workspacePath, "rev-parse", "--abbrev-ref", "HEAD");
        var branch = branchOut.Trim();

        var (exitCode, statusOut, _) = await RunGitAsync(workspacePath, "status", "--porcelain");
        var isClean = string.IsNullOrWhiteSpace(statusOut);

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

        return (branch, isClean, ahead, behind);
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
