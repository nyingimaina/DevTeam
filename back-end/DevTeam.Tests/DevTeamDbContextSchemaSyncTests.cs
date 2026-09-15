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
}
