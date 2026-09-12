using System.Text.Json;
using DevTeam.Broker.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class DevTeamDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public DevTeamDbContextTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private DevTeamDbContext CreateDbContext() => new(new DbContextOptionsBuilder<DevTeamDbContext>()
        .UseSqlite(_connection)
        .Options);

    [Fact]
    public void EnsureCreated_BuildsAllTables()
    {
        using var db = CreateDbContext();
        var tables = db.Database.SqlQuery<string>($$"""
             SELECT name FROM sqlite_master
             WHERE type='table' AND name NOT LIKE 'sqlite_%'
             AND name NOT LIKE '__EF%'
             """).ToList();

        Assert.Contains("Sessions", tables);
        Assert.Contains("Messages", tables);
        Assert.Contains("Parts", tables);
        Assert.Contains("AuditEvents", tables);
    }

    [Fact]
    public void Session_WithMessagesAndParts_RoundTrips()
    {
        Guid sessionId;
        using (var db = CreateDbContext())
        {
            var session = new DevTeamSession
            {
                WorkspacePath = @"C:\work\proj",
                AcpSessionId = "ses_abc",
                ModelId = "opencode/big-pickle",
            };
            var userMessage = new Message
            {
                Session = session,
                Role = "user",
                AcpMessageId = "msg_1",
                BodyText = "hello",
            };
            userMessage.Parts.Add(new Part
            {
                Message = userMessage,
                Kind = "text",
                Text = "hello",
            });
            session.Messages.Add(userMessage);

            var toolCall = new Part
            {
                Message = userMessage,
                Kind = "tool",
                ToolName = "bash",
                ToolCallId = "call_1",
                InputJson = JsonSerializer.SerializeToElement(new { command = "ls" }),
            };
            userMessage.Parts.Add(toolCall);

            db.Sessions.Add(session);
            db.SaveChanges();
            sessionId = session.Id;
        }

        using var verify = CreateDbContext();
        var loaded = verify.Sessions
            .Include(s => s.Messages)
            .ThenInclude(m => m.Parts)
            .Single(s => s.Id == sessionId);

        Assert.Equal(@"C:\work\proj", loaded.WorkspacePath);
        Assert.Equal("opencode/big-pickle", loaded.ModelId);
        Assert.Single(loaded.Messages);
        var message = loaded.Messages[0];
        Assert.Equal("user", message.Role);
        Assert.Equal(2, message.Parts.Count);

        var tool = message.Parts.Single(p => p.Kind == "tool");
        Assert.Equal("call_1", tool.ToolCallId);
        Assert.Equal("ls", tool.InputJson!.Value.GetProperty("command").GetString());
    }

    [Fact]
    public async Task AcpSessionId_IsUnique()
    {
        using var db = CreateDbContext();
        db.Sessions.Add(new DevTeamSession { WorkspacePath = @"C:\a", AcpSessionId = "ses_dup" });
        db.Sessions.Add(new DevTeamSession { WorkspacePath = @"C:\b", AcpSessionId = "ses_dup" });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditEvent_WithPayloadJson_RoundTrips()
    {
        using var db = CreateDbContext();
        var audit = new AuditEvent
        {
            SessionId = Guid.NewGuid(),
            Kind = "prompt",
            PayloadJson = JsonSerializer.SerializeToElement(new { tokens = 42 }),
        };
        db.AuditEvents.Add(audit);
        await db.SaveChangesAsync();

        var loaded = await db.AuditEvents.SingleAsync();
        Assert.Equal(42, loaded.PayloadJson!.Value.GetProperty("tokens").GetInt32());
    }
}