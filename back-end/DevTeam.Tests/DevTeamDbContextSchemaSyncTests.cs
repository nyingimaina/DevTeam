using DevTeam.Broker.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Tests;

public class DevTeamDbContextSchemaSyncTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DevTeamDbContext _db;

    public DevTeamDbContextSchemaSyncTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<DevTeamDbContext>().UseSqlite(_connection).Options;
        _db = new DevTeamDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Simulates a database file created before the Profiles feature existed: EnsureCreated()
    // is a no-op once a database file already exists, so any table added to the model later
    // never gets created for a pre-existing database. This drops the profile tables after the
    // initial EnsureCreated() to reproduce that "stale database" state.
    private void SimulateDatabasePredatingProfiles()
    {
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("DROP TABLE \"ProfilePrompts\";");
        _db.Database.ExecuteSqlRaw("DROP TABLE \"WorkspaceProfileSettings\";");
        _db.Database.ExecuteSqlRaw("DROP TABLE \"Profiles\";");
    }

    [Fact]
    public async Task EnsureAllTablesCreated_CreatesTablesMissingFromAnOlderDatabase()
    {
        SimulateDatabasePredatingProfiles();

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        Assert.Empty(await _db.Profiles.ToListAsync());
        Assert.Empty(await _db.ProfilePrompts.ToListAsync());
        Assert.Empty(await _db.WorkspaceProfileSettings.ToListAsync());
    }

    [Fact]
    public async Task EnsureAllTablesCreated_LeavesExistingDataInOtherTablesUntouched()
    {
        _db.Database.EnsureCreated();
        _db.WorkspaceGitSettings.Add(new WorkspaceGitSettings { WorkspacePath = @"C:\work\proj", CredentialName = "gh" });
        await _db.SaveChangesAsync();

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        var settings = await _db.WorkspaceGitSettings.SingleAsync();
        Assert.Equal("gh", settings.CredentialName);
    }

    [Fact]
    public void EnsureAllTablesCreated_AddsNotNullColumnsWithADefaultToAnOlderDatabase()
    {
        // Regression: SQLite refuses `ADD COLUMN ... NOT NULL` with no DEFAULT on an existing
        // table, which crashed startup for any database created before the loop-guard columns.
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("ALTER TABLE \"ReleaseStageRuns\" DROP COLUMN \"ConsecutiveFailures\";");
        _db.Database.ExecuteSqlRaw("ALTER TABLE \"ReleaseStageRuns\" DROP COLUMN \"AutoRetrySuppressed\";");

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        var columns = _db.Database
            .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('ReleaseStageRuns')")
            .ToList();
        Assert.Contains("ConsecutiveFailures", columns);
        Assert.Contains("AutoRetrySuppressed", columns);
    }

    [Fact]
    public async Task EnsureAllTablesCreated_NewlyCreatedTableAcceptsWrites()
    {
        SimulateDatabasePredatingProfiles();

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        _db.Profiles.Add(new Profile { Name = "Terse" });
        await _db.SaveChangesAsync();

        Assert.Single(await _db.Profiles.ToListAsync());
    }

    [Fact]
    public void EnsureAllTablesCreated_IsSafeToCallOnAFullyUpToDateDatabase()
    {
        _db.Database.EnsureCreated();

        var exception = Record.Exception(() => DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db));

        Assert.Null(exception);
    }

    // Simulates a table that already existed before a column was added to its entity later —
    // the same "stale database" problem as a missing table, but one level down. Rebuilds
    // WorkspaceGitSettings without CredentialName (a real column on the current model) to
    // prove the column-backfill path independently of any feature-specific column.
    [Fact]
    public async Task EnsureAllTablesCreated_AddsColumnsMissingFromAnExistingTable()
    {
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("DROP TABLE \"WorkspaceGitSettings\";");
        _db.Database.ExecuteSqlRaw(
            "CREATE TABLE \"WorkspaceGitSettings\" (\"WorkspacePath\" TEXT NOT NULL CONSTRAINT \"PK_WorkspaceGitSettings\" PRIMARY KEY);");

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        _db.WorkspaceGitSettings.Add(new WorkspaceGitSettings { WorkspacePath = @"C:\work\proj", CredentialName = "gh" });
        await _db.SaveChangesAsync();

        var settings = await _db.WorkspaceGitSettings.SingleAsync();
        Assert.Equal("gh", settings.CredentialName);
    }

    [Fact]
    public async Task EnsureAllTablesCreated_ColumnBackfillPreservesExistingRows()
    {
        _db.Database.EnsureCreated();
        _db.Database.ExecuteSqlRaw("DROP TABLE \"WorkspaceGitSettings\";");
        _db.Database.ExecuteSqlRaw(
            "CREATE TABLE \"WorkspaceGitSettings\" (\"WorkspacePath\" TEXT NOT NULL CONSTRAINT \"PK_WorkspaceGitSettings\" PRIMARY KEY);");
        _db.Database.ExecuteSqlRaw(
            "INSERT INTO \"WorkspaceGitSettings\" (\"WorkspacePath\") VALUES ('C:\\work\\proj');");

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        var settings = await _db.WorkspaceGitSettings.SingleAsync();
        Assert.Equal(@"C:\work\proj", settings.WorkspacePath);
        Assert.Null(settings.CredentialName);
    }

    // The real-world case this whole mechanism exists for: a NOT NULL bool column
    // (OverridesBuiltInPrompt) added to an entity whose table already has rows, on a
    // database that predates that column. Requires the HasDefaultValue(false) configured
    // in OnModelCreating — SQLite refuses ALTER TABLE ADD COLUMN NOT NULL without a default
    // on a non-empty table.
    [Fact]
    public async Task EnsureAllTablesCreated_BackfillsANotNullColumnWithItsConfiguredDefault()
    {
        _db.Database.EnsureCreated();
        var profile = new Profile { Name = "Terse" };
        _db.Profiles.Add(profile);
        await _db.SaveChangesAsync();

        _db.Database.ExecuteSqlRaw("DROP TABLE \"ProfilePrompts\";");
        _db.Database.ExecuteSqlRaw(
            "CREATE TABLE \"ProfilePrompts\" (" +
            "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_ProfilePrompts\" PRIMARY KEY, " +
            "\"ProfileId\" TEXT NOT NULL, " +
            "\"StageName\" TEXT NOT NULL, " +
            "\"PromptText\" TEXT NOT NULL);");
        _db.Database.ExecuteSqlRaw(
            $"INSERT INTO \"ProfilePrompts\" (\"Id\", \"ProfileId\", \"StageName\", \"PromptText\") " +
            $"VALUES ('{Guid.NewGuid()}', '{profile.Id}', 'business-analyst', 'Be brief.');");

        DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db);

        var prompt = await _db.ProfilePrompts.SingleAsync();
        Assert.Equal("Be brief.", prompt.PromptText);
        Assert.False(prompt.OverridesBuiltInPrompt);

        _db.ProfilePrompts.Add(new ProfilePrompt
        {
            ProfileId = profile.Id,
            StageName = "developer",
            PromptText = "Ship it.",
            OverridesBuiltInPrompt = true,
        });
        await _db.SaveChangesAsync();

        var added = await _db.ProfilePrompts.SingleAsync(p => p.StageName == "developer");
        Assert.True(added.OverridesBuiltInPrompt);
    }

    // Reproduces the crash from a devteam.db that predates the IsHotfix column (Part 7F):
    // 'Releases' already has rows, and SQLite refuses ALTER TABLE ADD COLUMN NOT NULL without
    // a configured default on a non-empty table. Same shape as the OverridesBuiltInPrompt case
    // above, for the DevTeamRelease.IsHotfix column specifically.
    [Fact]
    public async Task EnsureAllTablesCreated_BackfillsIsHotfixColumnWithoutCrashingOnExistingRows()
    {
        _db.Database.EnsureCreated();
        var release = new DevTeamRelease
        {
            WorkspacePath = @"C:\work\proj",
            Version = "1.0.0",
            BranchName = "release/login-form",
        };
        _db.Releases.Add(release);
        await _db.SaveChangesAsync();

        _db.Database.ExecuteSqlRaw("DROP TABLE \"Releases\";");
        _db.Database.ExecuteSqlRaw(
            "CREATE TABLE \"Releases\" (" +
            "\"Id\" TEXT NOT NULL CONSTRAINT \"PK_Releases\" PRIMARY KEY, " +
            "\"WorkspacePath\" TEXT NOT NULL, " +
            "\"Title\" TEXT NULL, " +
            "\"Version\" TEXT NOT NULL, " +
            "\"Status\" INTEGER NOT NULL, " +
            "\"BranchName\" TEXT NULL, " +
            "\"CreatedAt\" TEXT NOT NULL, " +
            "\"UpdatedAt\" TEXT NOT NULL);");
        _db.Database.ExecuteSqlRaw(
            $"INSERT INTO \"Releases\" (\"Id\", \"WorkspacePath\", \"Title\", \"Version\", \"Status\", \"BranchName\", \"CreatedAt\", \"UpdatedAt\") " +
            $"VALUES ('{release.Id}', '{release.WorkspacePath}', NULL, '{release.Version}', 0, '{release.BranchName}', " +
            $"'{release.CreatedAt:O}', '{release.UpdatedAt:O}');");

        var exception = Record.Exception(() => DevTeamDbContextSchemaSync.EnsureAllTablesCreated(_db));
        Assert.Null(exception);

        var reloaded = await _db.Releases.SingleAsync();
        Assert.False(reloaded.IsHotfix);

        _db.Releases.Add(new DevTeamRelease
        {
            WorkspacePath = @"C:\work\other",
            Version = "1.0.1",
            BranchName = "main",
            IsHotfix = true,
        });
        await _db.SaveChangesAsync();

        var hotfix = await _db.Releases.SingleAsync(r => r.IsHotfix);
        Assert.Equal("main", hotfix.BranchName);
    }
}
