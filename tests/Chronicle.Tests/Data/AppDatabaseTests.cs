using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chronicle.Data;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using Microsoft.Data.Sqlite;

namespace Chronicle.Tests.Data;

/// <summary>
/// Covers <see cref="AppDatabase.Initialize"/>: fresh-install schema
/// creation, and the forward-only recurrence migration that reconciles
/// databases created before the recurrence engine landed
/// (<c>MigrateRecurrenceColumns</c>). The migration is gated on PRAGMA
/// inspection, so it must be idempotent — safe to run on every startup.
///
/// Derives from <see cref="DatabaseTest"/> (not the initialized base) so
/// each test controls exactly when the schema is created: the migration
/// cases stage a pre-recurrence database first, then initialize over it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AppDatabaseTests : DatabaseTest
{
    [Fact]
    public void Initialize_OnFreshDatabase_CreatesCoreTables()
    {
        InitializeDatabase();

        Assert.True(TableExists("Calendars"));
        Assert.True(TableExists("Events"));
        Assert.True(TableExists("EventOverrides"));
    }

    [Fact]
    public void Initialize_OnFreshDatabase_CreatesRecurrenceEndIndex()
    {
        // The index lives in MigrateRecurrenceColumns, not Schema.sql (the
        // column does not exist yet when Schema.sql runs on a legacy DB).
        // On a fresh install it must still be created.
        InitializeDatabase();

        Assert.True(IndexExists("IX_Events_RecurrenceEndUtcCached"));
    }

    [Fact]
    public async Task Initialize_CalledTwiceOnPopulatedDatabase_PreservesData()
    {
        InitializeDatabase();

        var calendars = new CalendarRepository();
        await calendars.InsertAsync(
            new Calendar { Id = Guid.NewGuid(), Name = "Work" });

        // Re-running Initialize (every app startup does) must not wipe or
        // duplicate anything — CREATE TABLE IF NOT EXISTS + gated migration.
        AppDatabase.Initialize(DbPath);

        var all = await calendars.GetAllAsync();
        var calendar = Assert.Single(all);
        Assert.Equal("Work", calendar.Name);
    }

    [Fact]
    public void Migrate_RenamesRecurrenceRuleJsonToRecurrenceRule_PreservingData()
    {
        CreateLegacyEventsSchema();
        InsertLegacyEvent(recurrenceRuleJson: "FREQ=DAILY");

        InitializeDatabase();

        var columns = EventsColumns();
        Assert.Contains("RecurrenceRule", columns);
        Assert.DoesNotContain("RecurrenceRuleJson", columns);

        // The rename must carry the data across, not just the column name.
        Assert.Equal("FREQ=DAILY", ScalarString(
            "SELECT RecurrenceRule FROM Events LIMIT 1;"));
    }

    [Fact]
    public void Migrate_AddsRecurrenceColumns_ToLegacyDatabase()
    {
        CreateLegacyEventsSchema();

        InitializeDatabase();

        var columns = EventsColumns();
        Assert.Contains("RecurrenceExDatesUtc", columns);
        Assert.Contains("RecurrenceEndUtcCached", columns);
        Assert.Contains("TimeZoneId", columns);
    }

    [Fact]
    public void Migrate_OnLegacyDatabase_IsIdempotent()
    {
        CreateLegacyEventsSchema();

        // Two migration passes must converge on the same schema and never
        // throw (e.g. "duplicate column" from re-adding, or re-renaming a
        // column that no longer exists).
        InitializeDatabase();
        AppDatabase.Initialize(DbPath);

        var columns = EventsColumns();
        Assert.Contains("RecurrenceRule", columns);
        Assert.Contains("RecurrenceExDatesUtc", columns);
        Assert.Contains("RecurrenceEndUtcCached", columns);
        Assert.Contains("TimeZoneId", columns);
        Assert.DoesNotContain("RecurrenceRuleJson", columns);
        Assert.True(IndexExists("IX_Events_RecurrenceEndUtcCached"));
    }

    // --- Raw-SQL helpers (bypass the repositories to inspect / stage schema) ---

    // The pre-recurrence Events shape: RecurrenceRuleJson present, and none
    // of the columns the migration adds (RecurrenceRule, RecurrenceExDatesUtc,
    // RecurrenceEndUtcCached, TimeZoneId). Initialize's Schema.sql pass leaves
    // this table untouched (CREATE TABLE IF NOT EXISTS), then the migration
    // reconciles it.
    private void CreateLegacyEventsSchema()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
        """
        CREATE TABLE Calendars (
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            Color TEXT NOT NULL
        );

        CREATE TABLE Events (
            Id TEXT PRIMARY KEY,
            CalendarId TEXT NOT NULL,
            Title TEXT NOT NULL,
            Description TEXT,
            StartTimeUtc TEXT NOT NULL,
            EndTimeUtc TEXT NOT NULL,
            IsAllDay INTEGER NOT NULL,
            RecurrenceRuleJson TEXT,
            CreatedAtUtc TEXT NOT NULL,
            UpdatedAtUtc TEXT NOT NULL
        );
        """;
        command.ExecuteNonQuery();
    }

    private void InsertLegacyEvent(string recurrenceRuleJson)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
        """
        INSERT INTO Events (
            Id, CalendarId, Title, StartTimeUtc, EndTimeUtc, IsAllDay,
            RecurrenceRuleJson, CreatedAtUtc, UpdatedAtUtc
        ) VALUES (
            $id, $calendarId, 'Legacy', '2026-01-01T00:00:00.0000000Z',
            '2026-01-01T01:00:00.0000000Z', 0, $rule,
            '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'
        );
        """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$calendarId", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$rule", recurrenceRuleJson);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection;
    }

    private HashSet<string> EventsColumns()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Events);";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));
        return columns;
    }

    private bool TableExists(string name) => ObjectExists("table", name);

    private bool IndexExists(string name) => ObjectExists("index", name);

    private bool ObjectExists(string type, string name)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = $type AND name = $name;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private string? ScalarString(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = command.ExecuteScalar();
        return result == null || result is DBNull ? null : (string)result;
    }
}
