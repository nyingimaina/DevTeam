using System.Diagnostics;
using DevTeam.Broker.Rpc;
using DevTeam.Broker.Spoke;

namespace DevTeam.Tests;

/// <summary>
/// Exercises the real <c>opencode acp</c> binary over stdio. Skipped when opencode
/// is not on PATH so the suite stays portable; run with <c>dotnet test</c> on a
/// machine with opencode installed.
/// </summary>
public class OpencodeAcpIntegrationTests
{
    private static readonly string? OpenCodePath = OpenCodeLocator.ResolvePath();

    [Fact]
    public async Task FullConversation_AgainstRealOpencode()
    {
        if (OpenCodePath is null)
            return; // opencode not installed -> nothing to verify here

        var scratch = Path.Combine(Path.GetTempPath(), "devteam-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            using var spoke = new OpencodeAcpSpoke(new OpencodeAcpProcess(OpenCodePath, ["acp"]));

            var info = await spoke.InitializeAsync(CancellationToken.None);
            Assert.Equal("OpenCode", info.Name);

            var session = await spoke.NewSessionAsync(scratch, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(session.SessionId));
            Assert.Contains(session.ConfigOptions, o => o.Id == "model");

            await spoke.SetModelAsync(session.SessionId, "opencode/big-pickle", CancellationToken.None);

            var text = new List<string>();
            spoke.EventReceived += (_, e) =>
            {
                if (e is AgentTextDelta delta)
                    text.Add(delta.Text);
            };

            var result = await spoke.PromptAsync(
                session.SessionId,
                [new AgentPromptPart("text", "Reply with exactly the word: PICKLE")],
                CancellationToken.None);

            Assert.Equal("end_turn", result.StopReason);
            Assert.True(result.Usage is { TotalTokens: > 0 }, "expected usage recorded");
            Assert.NotEmpty(text);

            var second = await spoke.PromptAsync(
                session.SessionId,
                [new AgentPromptPart("text", "Now reply with exactly: DONE")],
                CancellationToken.None);
            Assert.Equal("end_turn", second.StopReason);
            Assert.True(text.Count >= 2, "expected deltas from both turns");
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    [Fact]
    public async Task SetModel_And_PermissionRequest_AgainstRealOpencode()
    {
        if (OpenCodePath is null)
            return;

        var scratch = Path.Combine(Path.GetTempPath(), "devteam-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);

        try
        {
            var permissionDecisions = new List<PermissionRequest>();
            using var spoke = new OpencodeAcpSpoke(
                new OpencodeAcpProcess(OpenCodePath, ["acp"]),
                new RecordingPolicy(permissionDecisions));

            await spoke.InitializeAsync(CancellationToken.None);

            var session = await spoke.NewSessionAsync(scratch, CancellationToken.None);

            await spoke.PromptAsync(
                session.SessionId,
                [new AgentPromptPart("text", "Reply with exactly: OK")],
                CancellationToken.None);

            // With a reject-all policy the broker must not crash even if a permission is asked.
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class RecordingPolicy(List<PermissionRequest> decisions) : IPermissionPolicy
    {
        public Task<PermissionVerdict> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
        {
            decisions.Add(request);
            return Task.FromResult(PermissionVerdict.Reject);
        }
    }
}