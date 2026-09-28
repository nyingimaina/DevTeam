using System.Diagnostics;
using System.Text.Json;

namespace DevTeam.Broker.Rpc;

/// <summary>
/// The opencode configuration DevTeam imposes on the agent process it starts.
/// </summary>
/// <remarks>
/// <para>
/// opencode's own defaults are <c>compaction.auto = true</c> and <c>compaction.prune = false</c>.
/// Auto only compacts once the context window has <em>overflowed</em>, and prune — the continuous
/// trimming of old tool output, which is the bulk of an exploratory agent's context — is off
/// entirely. A release interview therefore ran against a context that grew for the whole feature:
/// turns in the same session measured 13k, then 25k, then 143k cached tokens, and the average
/// duration of an 11+ tool-call turn was over two minutes.
/// </para>
///
/// <para>
/// Turning on prune fixes that growth without a model call and without summarizing anything, so
/// the conversation survives intact. Raising <c>reserved</c> moves the auto-compaction trigger off
/// the overflow boundary and gives it room to actually complete.
/// </para>
///
/// <para>
/// It is passed as <c>OPENCODE_CONFIG_CONTENT</c> on the child process rather than written to a
/// config file, so it applies to the agent DevTeam runs and to nothing else — a global
/// <c>opencode.json</c> would also change every interactive opencode session on the machine. An
/// explicitly configured value is never overwritten, so a user who set their own is not silently
/// overridden on every launch.
/// </para>
/// </remarks>
public static class CompactionPolicy
{
    /// <summary>opencode's env var for inline JSON configuration, merged with every other source.</summary>
    public const string EnvironmentVariable = "OPENCODE_CONFIG_CONTENT";

    /// <summary>
    /// Headroom kept free so auto-compaction runs before the window is full. opencode's default is
    /// 10000, which is not enough to finish a summarization pass on a large context.
    /// </summary>
    public const int ReservedTokens = 32768;

    public static string ConfigContent { get; } = JsonSerializer.Serialize(new
    {
        compaction = new { prune = true, reserved = ReservedTokens },
    });

    /// <summary>Applies the policy unless the environment already carries an explicit config.</summary>
    public static void Apply(ProcessStartInfo startInfo) => TryApply(startInfo, out _);

    /// <summary>
    /// Applies the policy and reports whether it is the one in effect, so a launch can be logged
    /// with the config the child actually received.
    /// </summary>
    public static bool TryApply(ProcessStartInfo startInfo, out string configContent)
    {
        if (startInfo.Environment.TryGetValue(EnvironmentVariable, out var existing)
            && !string.IsNullOrWhiteSpace(existing))
        {
            configContent = existing;
            return false;
        }

        startInfo.Environment[EnvironmentVariable] = ConfigContent;
        configContent = ConfigContent;
        return true;
    }
}
