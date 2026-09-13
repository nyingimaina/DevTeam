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
    [property: JsonPropertyName("targetBranch")] string? TargetBranch = null);

public record GitResponse(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("branch")] string? Branch = null,
    [property: JsonPropertyName("branches")] string[]? Branches = null,
    [property: JsonPropertyName("status")] string? Status = null,
    [property: JsonPropertyName("isRepo")] bool IsRepo = false,
    [property: JsonPropertyName("isClean")] bool IsClean = true,
    [property: JsonPropertyName("ahead")] int Ahead = 0,
    [property: JsonPropertyName("behind")] int Behind = 0);
