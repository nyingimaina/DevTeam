using System.Security.Cryptography;
using System.Text;

namespace DevTeam.Broker.Gates;

/// <summary>
/// Opt-in for a check whose answer depends only on files, so that a re-check inside the same stage
/// run can reuse the recorded verdict instead of paying for the run again.
/// <para>
/// The expensive checks - the scoped type check and tests, the full suite - are deterministic
/// functions of the tree they ran against. When nothing that could change their answer has changed,
/// re-running them buys nothing and costs minutes plus a full model turn, so the engine reuses the
/// recorded row. The key is a content hash rather than a timestamp or an attempt counter on purpose:
/// editing a single byte of one changed file must miss, and that has to be true across process
/// restarts and after the workspace has been committed, neither of which a counter survives.
/// </para>
/// <para>
/// Only ever implemented by gates that do side effects other than reading the workspace -
/// <c>test_run</c> writes test-run.json, so it must not be reused on a stale answer. See
/// <see cref="GateCache"/> for the hashing rules.
/// </para>
/// </summary>
public interface IReusableGate
{
    /// <summary>
    /// A key that is equal exactly when a re-run would produce the same answer, or null when this
    /// gate cannot be reused (missing inputs, or a check whose value moves for reasons no file
    /// captures). Returning null always falls back to running the gate.
    /// </summary>
    Task<string?> ReuseKeyAsync(GateRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Builds reuse keys. The rule is deliberately blunt: every input the check reads is hashed by
/// content, so "nothing relevant changed" is a fact about the bytes on disk rather than a guess
/// about how long ago the last run was.
/// </summary>
public static class GateCache
{
    /// <summary>
    /// The key for a check scoped to a set of changed files: the sorted (path, content hash) pairs,
    /// plus the base ref the set was computed against. Any edit to any of those files changes the
    /// key; a commit that leaves them byte-identical does not.
    /// </summary>
    public static string ForChangedFiles(
        string workspacePath, IEnumerable<string> paths, string? baseRef, string? scope)
    {
        var builder = new StringBuilder("changed:");
        builder.Append(Normalize(workspacePath)).Append('|')
            .Append(Normalize(baseRef)).Append('|').Append(Normalize(scope)).Append('|');
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
            builder.Append(Normalize(path)).Append('=').Append(HashFile(workspacePath, path)).Append(';');
        return Hash(builder.ToString());
    }

    /// <summary>
    /// The key for a check over the whole workspace, where the changed-file set is the honest
    /// signal of whether anything moved: the command, where it runs, and the content hash of every
    /// changed or untracked file.
    /// <para>
    /// Bounded by the feature's own change set rather than a full-tree walk, so the cost is
    /// proportional to what the developer actually touched. The trade-off is explicit: an edit to a
    /// shared file outside that set will not invalidate the key.
    /// </para>
    /// </summary>
    public static string ForWorkspace(
        string workspacePath, string? command, string? workingDirectory, IEnumerable<string> paths)
    {
        var builder = new StringBuilder("workspace:");
        builder.Append(Normalize(workspacePath)).Append('|')
            .Append(Normalize(command)).Append('|').Append(Normalize(workingDirectory)).Append('|');
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
            builder.Append(Normalize(path)).Append('=').Append(HashFile(workspacePath, path)).Append(';');
        return Hash(builder.ToString());
    }

    public static string Hash(string value)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    /// <summary>
    /// A missing or unreadable file hashes to a stable sentinel rather than throwing: the check
    /// itself decides what an absent file means, and a gate that cannot answer should fail on its
    /// own evidence instead of the cache silently treating "unreadable" as "unchanged".
    /// </summary>
    private static string HashFile(string workspacePath, string relativePath)
    {
        try
        {
            var full = Path.IsPathRooted(relativePath) ? relativePath : Path.Combine(workspacePath, relativePath);
            using var stream = File.OpenRead(full);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static string Normalize(string? value)
        => (value ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
}
