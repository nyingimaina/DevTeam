using System.Diagnostics;
using System.Text.Json;

namespace DevTeam.Broker.Git;

public interface IGitService
{
    Task<GitResponse> InitAsync(string workspacePath, CancellationToken ct = default);
    Task<GitResponse> StatusAsync(string workspacePath, CancellationToken ct = default);
    Task<GitResponse> EnsureBranchAsync(string workspacePath, string branchName, CancellationToken ct = default);
    Task<GitResponse> CommitAsync(string workspacePath, string message, CancellationToken ct = default);
    Task<GitResponse> MergeAsync(string workspacePath, string sourceBranch, string targetBranch, CancellationToken ct = default);
    Task<GitResponse> BranchAsync(string workspacePath, string branchName, CancellationToken ct = default);
    Task<GitResponse> CheckoutAsync(string workspacePath, string branchName, CancellationToken ct = default);
    Task<GitResponse> LogAsync(string workspacePath, CancellationToken ct = default);
}

public sealed class GitService : IGitService, IDisposable
{
    private readonly ILogger<GitService> _logger;
    private readonly string _cliPath;
    private Process? _process;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
        _cliPath = FindCliPath();
    }

    public async Task<GitResponse> InitAsync(string workspacePath, CancellationToken ct = default)
        => await SendAsync(new GitRequest("init", workspacePath), ct);

    public async Task<GitResponse> StatusAsync(string workspacePath, CancellationToken ct = default)
        => await SendAsync(new GitRequest("status", workspacePath), ct);

    public async Task<GitResponse> EnsureBranchAsync(string workspacePath, string branchName, CancellationToken ct = default)
        => await SendAsync(new GitRequest("ensure-branch", workspacePath, branchName), ct);

    public async Task<GitResponse> CommitAsync(string workspacePath, string message, CancellationToken ct = default)
        => await SendAsync(new GitRequest("commit", workspacePath, Message: message), ct);

    public async Task<GitResponse> MergeAsync(string workspacePath, string sourceBranch, string targetBranch, CancellationToken ct = default)
        => await SendAsync(new GitRequest("merge", workspacePath, SourceBranch: sourceBranch, TargetBranch: targetBranch), ct);

    public async Task<GitResponse> BranchAsync(string workspacePath, string branchName, CancellationToken ct = default)
        => await SendAsync(new GitRequest("branch", workspacePath, branchName), ct);

    public async Task<GitResponse> CheckoutAsync(string workspacePath, string branchName, CancellationToken ct = default)
        => await SendAsync(new GitRequest("checkout", workspacePath, branchName), ct);

    public async Task<GitResponse> LogAsync(string workspacePath, CancellationToken ct = default)
        => await SendAsync(new GitRequest("log", workspacePath), ct);

    private async Task<GitResponse> SendAsync(GitRequest request, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            await EnsureProcessAsync(ct);
            var json = JsonSerializer.Serialize(request, JsonOptions);
            _logger.LogDebug("GitCli <- {Json}", json);

            await _process!.StandardInput.WriteLineAsync(json);
            await _process.StandardInput.FlushAsync(ct);

            var line = await _process.StandardOutput.ReadLineAsync(ct);
            if (line is null)
            {
                _logger.LogWarning("GitCli process exited");
                _process = null;
                return new GitResponse(false, "GitCli process exited unexpectedly");
            }

            _logger.LogDebug("GitCli -> {Line}", line);
            return JsonSerializer.Deserialize<GitResponse>(line, JsonOptions)
                   ?? new GitResponse(false, "Invalid response from GitCli");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GitCli communication failed");
            _process = null;
            return new GitResponse(false, ex.Message);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task EnsureProcessAsync(CancellationToken ct)
    {
        if (_process is { HasExited: false }) return;

        _logger.LogInformation("Starting GitCli at {Path}", _cliPath);
        var psi = new ProcessStartInfo(_cliPath)
        {
            WorkingDirectory = Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start GitCli");

        _ = Task.Run(async () =>
        {
            while (!_process.StandardError.EndOfStream)
            {
                var err = await _process.StandardError.ReadLineAsync(ct);
                if (!string.IsNullOrEmpty(err))
                    _logger.LogWarning("GitCli stderr: {Error}", err);
            }
        }, ct);

        await Task.Delay(100, ct);
    }

    private static string FindCliPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "DevTeam.GitCli.dll"),
            Path.Combine(baseDir, "DevTeam.GitCli", "bin", "Debug", "net10.0", "DevTeam.GitCli.dll"),
            Path.Combine(baseDir, "DevTeam.GitCli", "bin", "Release", "net10.0", "DevTeam.GitCli.dll"),
        };

        foreach (var candidate in candidates)
        {
            var full = Path.GetFullPath(candidate);
            if (File.Exists(full)) return full;
        }

        return Path.Combine(baseDir, "DevTeam.GitCli.dll");
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
        {
            try { _process.Kill(); } catch { /* ignore */ }
        }
        _process?.Dispose();
        _lock.Dispose();
    }
}

public record GitRequest(
    string Command,
    string? WorkspacePath = null,
    string? BranchName = null,
    string? Message = null,
    string? SourceBranch = null,
    string? TargetBranch = null);

public record GitResponse(
    bool Success,
    string? Message = null,
    string? Branch = null,
    string[]? Branches = null,
    string? Status = null,
    bool IsRepo = false,
    bool IsClean = true,
    int Ahead = 0,
    int Behind = 0,
    GitCommit[]? Commits = null);

public record GitCommit(
    string Hash,
    string ShortHash,
    string Message,
    string Author,
    string Date,
    string[] Parents,
    string? Branch = null,
    string[]? Tags = null);
