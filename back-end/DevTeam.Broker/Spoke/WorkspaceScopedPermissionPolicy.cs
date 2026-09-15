using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Server;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Spoke;

/// <summary>
/// Auto-allows a tool call whose rawInput stays within the calling session's own
/// workspace and doesn't match a destructive-command pattern; rejects anything that
/// reaches outside that workspace or looks destructive. Looks up the workspace by the
/// ACP session id on <see cref="PermissionRequest.SessionId"/> so one policy instance
/// can safely serve every release, each with its own workspace.
///
/// Inspects rawInput for common path fields (filePath, path, file, cwd, directory) and
/// command fields (command, cmd, script). A tool call with neither — most read-only
/// tools (list directory, search, etc.) — is allowed by default: rejecting unrecognized
/// shapes would just reproduce the RejectAllPermissionPolicy hang this replaces.
/// </summary>
public sealed class WorkspaceScopedPermissionPolicy : IPermissionPolicy
{
    private static readonly string[] PathFields = ["filePath", "path", "file", "cwd", "directory"];
    private static readonly string[] CommandFields = ["command", "cmd", "script"];
    private static readonly string[] DestructivePatterns =
    [
        "rm -rf", "rm -fr", "rd /s", "del /f", "del /s", "format ", "mkfs", "dd if=",
        ":(){ :|:& };:", "shutdown", "reboot", "diskpart", "> /dev/sd", "git push --force", "git push -f",
    ];

    private readonly IDbContextFactory<DevTeamDbContext> _dbFactory;
    private readonly ILogger<WorkspaceScopedPermissionPolicy> _logger;

    public WorkspaceScopedPermissionPolicy(
        IDbContextFactory<DevTeamDbContext> dbFactory, ILogger<WorkspaceScopedPermissionPolicy> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<PermissionVerdict> DecideAsync(PermissionRequest request, CancellationToken cancellationToken)
    {
        if (request.RawInput is not { } input || input.ValueKind != JsonValueKind.Object)
            return PermissionVerdict.AllowOnce;

        var scope = await ResolveWorkspaceScopeAsync(request.SessionId, cancellationToken);
        if (scope is null)
        {
            _logger.LogWarning(
                "No known workspace for ACP session {SessionId}; allowing tool call {ToolCallId} by default.",
                request.SessionId, request.ToolCallId);
            return PermissionVerdict.AllowOnce;
        }

        foreach (var field in PathFields)
        {
            if (!TryGetString(input, field, out var candidate)) continue;

            // Defense-in-depth: the full-workspace check always runs first and unconditionally —
            // a bug in the narrower per-role check below must never grant access outside the
            // workspace entirely.
            if (!PathContainment.IsWithinWorkspace(scope.Value.Root, candidate))
                return PermissionVerdict.Reject;

            if (scope.Value.AllowedPrefixes is { } prefixes)
            {
                // Resolve once against the workspace root so a relative candidate is checked
                // against each prefix as an absolute path, not re-resolved relative to the prefix.
                var resolvedCandidate = Path.IsPathRooted(candidate)
                    ? Path.GetFullPath(candidate)
                    : Path.GetFullPath(Path.Combine(scope.Value.Root, candidate));

                if (!prefixes.Any(prefix => PathContainment.IsWithinWorkspace(Path.Combine(scope.Value.Root, prefix), resolvedCandidate)))
                    return PermissionVerdict.Reject;
            }
        }

        foreach (var field in CommandFields)
        {
            if (TryGetString(input, field, out var command) && IsDestructive(command))
                return PermissionVerdict.Reject;
        }

        return PermissionVerdict.AllowOnce;
    }

    private readonly record struct WorkspaceScope(string Root, IReadOnlyList<string>? AllowedPrefixes);

    private async Task<WorkspaceScope?> ResolveWorkspaceScopeAsync(string acpSessionId, CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.Sessions
            .SingleOrDefaultAsync(s => s.AcpSessionId == acpSessionId, cancellationToken);
        if (session is null) return null;

        var prefixes = session.AllowedWritePrefixesJson is null
            ? null
            : JsonSerializer.Deserialize<List<string>>(session.AllowedWritePrefixesJson);
        return new WorkspaceScope(Path.GetFullPath(session.WorkspacePath), prefixes);
    }

    private static bool IsDestructive(string command)
    {
        foreach (var pattern in DestructivePatterns)
        {
            if (command.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool TryGetString(JsonElement obj, string property, out string value)
    {
        if (obj.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }
        value = string.Empty;
        return false;
    }
}
