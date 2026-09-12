using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DevTeam.Broker.Domain;

public sealed class DevTeamDbContext : DbContext
{
    public DevTeamDbContext(DbContextOptions<DevTeamDbContext> options)
        : base(options)
    {
    }

    public DbSet<DevTeamSession> Sessions => Set<DevTeamSession>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevTeamSession>(session =>
        {
            session.HasKey(e => e.Id);
            session.Property(e => e.WorkspacePath).IsRequired();
            session.Property(e => e.AcpSessionId).IsRequired();
            session.HasIndex(e => e.AcpSessionId).IsUnique();
            session.Property(e => e.Title).HasMaxLength(512);
            session.Property(e => e.ModelId).HasMaxLength(256);
        });

        modelBuilder.Entity<Message>(message =>
        {
            message.HasKey(e => e.Id);
            message.HasOne(e => e.Session)
                .WithMany(s => s.Messages)
                .HasForeignKey(e => e.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            message.Property(e => e.Role).IsRequired();
            message.Property(e => e.BodyText).HasColumnType("TEXT");
        });

        modelBuilder.Entity<Part>(part =>
        {
            part.HasKey(e => e.Id);
            part.HasOne(e => e.Message)
                .WithMany(m => m.Parts)
                .HasForeignKey(e => e.MessageId)
                .OnDelete(DeleteBehavior.Cascade);
            part.Property(e => e.Kind).IsRequired();
            var json = new ValueConverter<JsonElement?, string>(
                v => v.HasValue ? v.Value.GetRawText() : string.Empty,
                v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<JsonElement>(v));
            part.Property(e => e.InputJson).HasConversion(json).HasColumnType("TEXT");
            part.Property(e => e.OutputJson).HasConversion(json).HasColumnType("TEXT");
        });

        modelBuilder.Entity<AuditEvent>(audit =>
        {
            audit.HasKey(e => e.Id);
            audit.Property(e => e.Kind).IsRequired();
            var json = new ValueConverter<JsonElement?, string>(
                v => v.HasValue ? v.Value.GetRawText() : string.Empty,
                v => string.IsNullOrEmpty(v) ? null : JsonSerializer.Deserialize<JsonElement>(v));
            audit.Property(e => e.PayloadJson).HasConversion(json).HasColumnType("TEXT");
            audit.HasIndex(e => new { e.SessionId, e.CreatedAt });
        });
    }
}