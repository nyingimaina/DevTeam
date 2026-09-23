using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevTeam.GitCli;

public static class Program
{
    public static async Task<int> Main()
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var handler = new GitCommandHandler();

        while (true)
        {
            var line = await Console.In.ReadLineAsync();
            if (line is null) break;

            GitRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<GitRequest>(line, JsonOptions);
            }
            catch
            {
                await WriteResponseAsync(new GitResponse(false, "Invalid JSON"));
                continue;
            }

            if (request is null)
            {
                await WriteResponseAsync(new GitResponse(false, "Null request"));
                continue;
            }

            var response = await handler.HandleAsync(request);
            await WriteResponseAsync(response);
        }

        return 0;
    }

    private static async Task WriteResponseAsync(GitResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOptions);
        await Console.Out.WriteLineAsync(json);
        await Console.Out.FlushAsync();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public record GitRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("workspacePath")] string? WorkspacePath = null,
    [property: JsonPropertyName("branchName")] string? BranchName = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("sourceBranch")] string? SourceBranch = null,
    [property: JsonPropertyName("targetBranch")] string? TargetBranch = null,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl = null,
    [property: JsonPropertyName("authToken")] string? AuthToken = null);

// Message doubles as the stash tag for stash-push/stash-apply/stash-drop — the caller-supplied
// text (e.g. "devteam-feature-<id>") that identifies a specific stash entry regardless of its
// position in the stack, since a workspace can accumulate stashes for several parked features.

public record GitResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("branches")] string[]? Branches = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("isRepo")] bool IsRepo = false,
    [property: JsonPropertyName("isClean")] bool IsClean = true,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0,
    [property: JsonPropertyName("commits")] GitCommit[]? Commits = null,
    [property: JsonPropertyName("hasRemote")] bool HasRemote = false,
    [property: JsonPropertyName("remoteUrl")] string? RemoteUrl = null,
    [property: JsonPropertyName("changedFiles")] string[]? ChangedFiles = null,
    [property: JsonPropertyName("stashEntries")] string[]? StashEntries = null,
    // Populated on a merge or stash-apply failure that left conflict markers in the working
    // tree (git diff --name-only --diff-filter=U) — never populated on other kinds of failure.
    [property: JsonPropertyName("conflictedFiles")] string[]? ConflictedFiles = null,
    // The resolved commit hash for a "rev-parse" request — see GitCommandHandler.RevParseAsync.
    [property: JsonPropertyName("commitSha")] string? CommitSha = null);

public record GitCommit(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("shortHash")] string ShortHash,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("parents")] string[] Parents,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("tags")] string[]? Tags = null);
