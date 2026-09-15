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
    public DbSet<DevTeamRelease> Releases => Set<DevTeamRelease>();
    public DbSet<ReleaseFeature> ReleaseFeatures => Set<ReleaseFeature>();
    public DbSet<ReleaseRequirement> ReleaseRequirements => Set<ReleaseRequirement>();
    public DbSet<FeatureSlice> FeatureSlices => Set<FeatureSlice>();
    public DbSet<ReleaseStageRun> ReleaseStageRuns => Set<ReleaseStageRun>();
    public DbSet<ReleaseGateCheck> ReleaseGateChecks => Set<ReleaseGateCheck>();
    public DbSet<ReviewFinding> ReviewFindings => Set<ReviewFinding>();
    public DbSet<ReleaseSignoff> ReleaseSignoffs => Set<ReleaseSignoff>();
    public DbSet<ReleaseGuidanceNote> ReleaseGuidanceNotes => Set<ReleaseGuidanceNote>();
    public DbSet<ReleaseUsageLedger> ReleaseUsageLedgers => Set<ReleaseUsageLedger>();
    public DbSet<ReleaseFlowPosition> ReleaseFlowPositions => Set<ReleaseFlowPosition>();
    public DbSet<WorkspaceGitSettings> WorkspaceGitSettings => Set<WorkspaceGitSettings>();

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
            session.Property(e => e.ModeId).HasMaxLength(256);
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

        ConfigureReleaseEntities(modelBuilder);

        modelBuilder.Entity<WorkspaceGitSettings>(settings =>
        {
            settings.HasKey(e => e.WorkspacePath);
            settings.Property(e => e.CredentialName).HasMaxLength(256);
        });
    }

    private static void ConfigureReleaseEntities(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DevTeamRelease>(release =>
        {
            release.HasKey(e => e.Id);
            release.Property(e => e.WorkspacePath).IsRequired();
            release.Property(e => e.Title).HasMaxLength(512);
            release.Property(e => e.Version).IsRequired().HasMaxLength(64);
            release.Property(e => e.BranchName).HasMaxLength(256);
            release.HasMany(e => e.Features)
                .WithOne(f => f.Release)
                .HasForeignKey(f => f.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseFlowPosition>(position =>
        {
            position.HasKey(e => e.Id);
            position.HasIndex(e => e.ReleaseFeatureId).IsUnique();
            position.Property(e => e.CurrentStageName).HasMaxLength(128);
            position.HasOne(e => e.Feature)
                .WithOne(f => f.FlowPosition)
                .HasForeignKey<ReleaseFlowPosition>(e => e.ReleaseFeatureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseFeature>(feature =>
        {
            feature.HasKey(e => e.Id);
            feature.Property(e => e.Key).IsRequired().HasMaxLength(128);
            feature.Property(e => e.Title).IsRequired().HasMaxLength(256);
            feature.Property(e => e.Description).HasColumnType("TEXT");
            feature.Property(e => e.BranchName).HasMaxLength(256);
            feature.HasIndex(e => new { e.ReleaseId, e.Key }).IsUnique();
            feature.HasMany(e => e.Requirements)
                .WithOne(r => r.Feature)
                .HasForeignKey(r => r.FeatureId)
                .OnDelete(DeleteBehavior.Cascade);
            feature.HasMany(e => e.StageRuns)
                .WithOne(r => r.Feature)
                .HasForeignKey(r => r.ReleaseFeatureId)
                .OnDelete(DeleteBehavior.Cascade);
            feature.HasMany(e => e.Signoffs)
                .WithOne(s => s.Feature)
                .HasForeignKey(s => s.ReleaseFeatureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FeatureSlice>(slice =>
        {
            slice.HasKey(e => e.Id);
            slice.HasIndex(e => e.FeatureId).IsUnique();
            slice.Property(e => e.ArtifactDirectory).HasMaxLength(1024);
            slice.Property(e => e.CodePathBack).HasMaxLength(1024);
            slice.Property(e => e.CodePathFront).HasMaxLength(1024);
            slice.HasOne(e => e.Feature)
                .WithOne(f => f.Slice)
                .HasForeignKey<FeatureSlice>(e => e.FeatureId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseRequirement>(requirement =>
        {
            requirement.HasKey(e => e.Id);
            requirement.Property(e => e.ReqId).IsRequired().HasMaxLength(64);
            requirement.Property(e => e.Title).IsRequired().HasMaxLength(256);
            requirement.Property(e => e.Description).HasColumnType("TEXT");
            requirement.Property(e => e.AcceptanceCriteria).HasColumnType("TEXT");
            requirement.HasIndex(e => new { e.FeatureId, e.ReqId }).IsUnique();
        });

        modelBuilder.Entity<ReleaseStageRun>(stage =>
        {
            stage.HasKey(e => e.Id);
            stage.Property(e => e.StageName).IsRequired().HasMaxLength(128);
            stage.Property(e => e.Summary).HasColumnType("TEXT");
            stage.HasIndex(e => new { e.ReleaseFeatureId, e.StageName, e.Attempt }).IsUnique();
            stage.HasOne(e => e.Session)
                .WithMany()
                .HasForeignKey(e => e.SessionId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.SetNull);
            stage.HasMany(e => e.GateChecks)
                .WithOne(g => g.StageRun)
                .HasForeignKey(g => g.StageRunId)
                .OnDelete(DeleteBehavior.Cascade);
            stage.HasMany(e => e.Findings)
                .WithOne(f => f.StageRun)
                .HasForeignKey(f => f.StageRunId)
                .OnDelete(DeleteBehavior.Cascade);
            stage.HasMany(e => e.GuidanceNotes)
                .WithOne(n => n.StageRun)
                .HasForeignKey(n => n.StageRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ReleaseGateCheck>(gate =>
        {
            gate.HasKey(e => e.Id);
            gate.Property(e => e.Name).IsRequired().HasMaxLength(128);
            gate.Property(e => e.EvidenceText).HasColumnType("TEXT");
            gate.Property(e => e.EvidencePath).HasMaxLength(1024);
        });

        modelBuilder.Entity<ReviewFinding>(finding =>
        {
            finding.HasKey(e => e.Id);
            finding.Property(e => e.Target).IsRequired().HasMaxLength(128);
            finding.Property(e => e.RequirementRef).HasMaxLength(64);
            finding.Property(e => e.Summary).IsRequired().HasColumnType("TEXT");
            finding.Property(e => e.EvidencePath).HasMaxLength(1024);
            finding.Property(e => e.ResolutionNote).HasColumnType("TEXT");
            finding.HasIndex(e => new { e.StageRunId, e.Status });
            finding.HasOne(e => e.ResolverStageRun)
                .WithMany()
                .HasForeignKey(e => e.ResolverStageRunId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<ReleaseSignoff>(signoff =>
        {
            signoff.HasKey(e => e.Id);
            signoff.Property(e => e.StageName).IsRequired().HasMaxLength(128);
            signoff.Property(e => e.ApprovedBy).HasMaxLength(256);
            signoff.Property(e => e.Comment).HasColumnType("TEXT");
            signoff.HasIndex(e => new { e.ReleaseFeatureId, e.StageName }).IsUnique();
        });

        modelBuilder.Entity<ReleaseGuidanceNote>(note =>
        {
            note.HasKey(e => e.Id);
            note.Property(e => e.Text).IsRequired().HasColumnType("TEXT");
            note.Property(e => e.AddedBy).HasMaxLength(256);
        });

        modelBuilder.Entity<ReleaseUsageLedger>(ledger =>
        {
            ledger.HasKey(e => e.Id);
            ledger.HasOne(e => e.StageRun)
                .WithMany()
                .HasForeignKey(e => e.StageRunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}