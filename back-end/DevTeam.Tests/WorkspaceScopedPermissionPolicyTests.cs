using System.Text.Json;
using DevTeam.Broker.Domain;
using DevTeam.Broker.Spoke;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DevTeam.Tests;

public class WorkspaceScopedPermissionPolicyTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public WorkspaceScopedPermissionPolicyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateFactory().CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DevTeamDbContext> CreateFactory() => new SqliteDbContextFactory(_connection);

    private async Task SeedSessionAsync(string acpSessionId, string workspacePath, IReadOnlyList<string>? allowedWritePrefixes = null)
    {
        await using var db = CreateFactory().CreateDbContext();
        db.Sessions.Add(new DevTeamSession
        {
            AcpSessionId = acpSessionId,
            WorkspacePath = workspacePath,
            AllowedWritePrefixesJson = allowedWritePrefixes is null ? null : JsonSerializer.Serialize(allowedWritePrefixes),
        });
        await db.SaveChangesAsync();
    }

    private WorkspaceScopedPermissionPolicy CreatePolicy()
        => new(CreateFactory(), NullLogger<WorkspaceScopedPermissionPolicy>.Instance);

    private static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    [Fact]
    public async Task DecideAsync_FileWithinSessionWorkspace_Allows()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\src\\App.tsx"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_FileOutsideSessionWorkspace_Rejects()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\Windows\\System32\\config.sys"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_PathTraversalOutsideWorkspace_Rejects()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\..\\..\\secrets.txt"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_SiblingDirectoryWithSamePrefix_Rejects()
    {
        // Guards against a naive StartsWith("C:\work\proj") matching "C:\work\proj2".
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj2\\secrets.txt"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_RelativePathWithinWorkspace_Allows()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"path":"src/App.tsx"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_DestructiveCommand_Rejects()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Run command", Json("""{"command":"rm -rf /"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_SafeCommand_Allows()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Run command", Json("""{"command":"npm test"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_UnrecognizedToolShape_AllowsByDefault()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "List directory", Json("""{"recursive":true}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_NullRawInput_AllowsByDefault()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj");
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Some tool", null);

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_UnknownSession_AllowsByDefault()
    {
        var policy = CreatePolicy();
        var request = new PermissionRequest("no-such-session", "t1", "Write file", Json("""{"filePath":"C:\\anywhere\\x.txt"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_PathOutsideAllowedPrefixes_ButInsideWorkspace_Rejects()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj", ["devteam/features/login"]);
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\back-end\\Program.cs"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_PathInsideAnyAllowedPrefix_Allows()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj", ["devteam/features/login", "back-end/Features/login"]);
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\devteam\\features\\login\\requirements.md"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_NoAllowedPrefixesSet_UnrestrictedAsToday()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj", allowedWritePrefixes: null);
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\back-end\\Program.cs"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, verdict);
    }

    [Fact]
    public async Task DecideAsync_RelativePathResolvedAgainstWorkspaceRoot_RespectsPrefixRestriction()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj", ["devteam/features/login"]);
        var policy = CreatePolicy();
        var request = new PermissionRequest("acp-1", "t1", "Write file", Json("""{"path":"back-end/Program.cs"}"""));

        var verdict = await policy.DecideAsync(request, CancellationToken.None);

        Assert.Equal(PermissionVerdict.Reject, verdict);
    }

    [Fact]
    public async Task DecideAsync_DeveloperPrefixes_AllowsManifestCodePathsAndSharedFiles_RejectsElsewhere()
    {
        await SeedSessionAsync("acp-1", @"C:\work\proj",
            ["devteam/features/login", "back-end/Features/login", "front-end/app/login", "Program.cs"]);
        var policy = CreatePolicy();

        var backendWrite = await policy.DecideAsync(
            new PermissionRequest("acp-1", "t1", "Write file", Json("""{"filePath":"C:\\work\\proj\\back-end\\Features\\login\\LoginHandler.cs"}""")),
            CancellationToken.None);
        var frontendWrite = await policy.DecideAsync(
            new PermissionRequest("acp-1", "t2", "Write file", Json("""{"filePath":"C:\\work\\proj\\front-end\\app\\login\\Login.tsx"}""")),
            CancellationToken.None);
        var sharedWrite = await policy.DecideAsync(
            new PermissionRequest("acp-1", "t3", "Write file", Json("""{"filePath":"C:\\work\\proj\\Program.cs"}""")),
            CancellationToken.None);
        var docsWrite = await policy.DecideAsync(
            new PermissionRequest("acp-1", "t4", "Write file", Json("""{"filePath":"C:\\work\\proj\\devteam\\features\\login\\context.md"}""")),
            CancellationToken.None);
        var elsewhereWrite = await policy.DecideAsync(
            new PermissionRequest("acp-1", "t5", "Write file", Json("""{"filePath":"C:\\work\\proj\\back-end\\Features\\billing\\Billing.cs"}""")),
            CancellationToken.None);

        Assert.Equal(PermissionVerdict.AllowOnce, backendWrite);
        Assert.Equal(PermissionVerdict.AllowOnce, frontendWrite);
        Assert.Equal(PermissionVerdict.AllowOnce, sharedWrite);
        Assert.Equal(PermissionVerdict.AllowOnce, docsWrite);
        Assert.Equal(PermissionVerdict.Reject, elsewhereWrite);
    }

    private sealed class SqliteDbContextFactory(SqliteConnection connection) : IDbContextFactory<DevTeamDbContext>
    {
        public DevTeamDbContext CreateDbContext()
            => new(new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(connection).Options);
    }
}
