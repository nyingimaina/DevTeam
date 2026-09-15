using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace DevTeam.Broker.Domain;

// This project intentionally has no EF Core migrations — Program.cs just calls
// EnsureCreated(). EnsureCreated() only builds the schema for a brand-new database
// file; once the file already exists it's a no-op, so any entity (or property) added
// after a database has been created in the field never gets its table/column (e.g. a
// dev's existing devteam.db predating the Profiles feature, or predating a column added
// to an entity that already shipped). This fills that gap two ways: creating whichever
// tables (and their indexes) are missing entirely, and — for tables that already exist —
// adding whichever columns are missing, both reusing EF's own generated DDL so the shape
// always matches the model.
public static class DevTeamDbContextSchemaSync
{
    public static void EnsureAllTablesCreated(DevTeamDbContext db)
    {
        var existingTables = db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type = 'table'")
            .ToList();

        var statements = db.Database.GenerateCreateScript()
            .Split(';')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        foreach (var statement in statements)
        {
            var tableName = ExtractTableName(statement);
            if (tableName is null) continue;

            if (!existingTables.Contains(tableName, StringComparer.OrdinalIgnoreCase))
            {
                db.Database.ExecuteSqlRaw(statement);
                continue;
            }

            if (statement.StartsWith("CREATE TABLE", StringComparison.Ordinal))
                EnsureColumnsPresent(db, tableName, statement);
        }
    }

    // tableName/definition are never external input — both come from EF's own generated
    // DDL (the model's own table/column names), never from a caller or request, so the
    // interpolation EF1002 warns about isn't a real injection surface here.
#pragma warning disable EF1002
    private static void EnsureColumnsPresent(DevTeamDbContext db, string tableName, string createTableStatement)
    {
        var existingColumns = db.Database
            .SqlQueryRaw<string>($"SELECT name FROM pragma_table_info('{tableName}')")
            .ToList();

        foreach (var (name, definition) in ExtractColumnDefinitions(createTableStatement))
        {
            if (existingColumns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            db.Database.ExecuteSqlRaw($"ALTER TABLE \"{tableName}\" ADD COLUMN {definition}");
        }
    }
#pragma warning restore EF1002

    private static IEnumerable<(string Name, string Definition)> ExtractColumnDefinitions(string createTableStatement)
    {
        var bodyMatch = Regex.Match(createTableStatement, "CREATE TABLE \"[^\"]+\"\\s*\\((?<body>.*)\\)\\s*$", RegexOptions.Singleline);
        if (!bodyMatch.Success) yield break;

        foreach (var fragment in SplitTopLevel(bodyMatch.Groups["body"].Value))
        {
            var trimmed = fragment.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase)) continue;

            var columnMatch = Regex.Match(trimmed, "^\"(?<name>[^\"]+)\"\\s+.+$", RegexOptions.Singleline);
            if (!columnMatch.Success) continue;

            yield return (columnMatch.Groups["name"].Value, trimmed);
        }
    }

    // Splits a CREATE TABLE body on top-level commas only — FOREIGN KEY (...) REFERENCES ...
    // fragments contain their own commas/parens that must not be treated as separators.
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i];
                    start = i + 1;
                    break;
            }
        }
        yield return body[start..];
    }

    private static string? ExtractTableName(string statement)
    {
        var createTable = Regex.Match(statement, "CREATE TABLE \"(?<name>[^\"]+)\"");
        if (createTable.Success) return createTable.Groups["name"].Value;

        var indexOn = Regex.Match(statement, "\\sON\\s+\"(?<name>[^\"]+)\"");
        if (indexOn.Success) return indexOn.Groups["name"].Value;

        return null;
    }
}
