using System.Text.Json;

namespace DevTeam.Broker.Domain;

public sealed class DevTeamSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string WorkspacePath { get; set; } = string.Empty;
    public string AcpSessionId { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? ModelId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Message> Messages { get; set; } = [];
}

public sealed class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public DevTeamSession Session { get; set; } = null!;
    public string Role { get; set; } = string.Empty;
    public string? AcpMessageId { get; set; }
    public string? ProviderModelId { get; set; }
    public string? BodyText { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<Part> Parts { get; set; } = [];
}

public sealed class Part
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public Message Message { get; set; } = null!;
    public string Kind { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? ToolName { get; set; }
    public string? ToolCallId { get; set; }
    public string? ErrorText { get; set; }
    public JsonElement? InputJson { get; set; }
    public JsonElement? OutputJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public JsonElement? PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}