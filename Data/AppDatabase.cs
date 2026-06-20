using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;

namespace Chronicle.Data;

public static class AppDatabase
{
    private static readonly string DbPath =
        Path.Combine(
            ApplicationData.Current.LocalFolder.Path,
            "chronicle.db");

    private static readonly string SchemaPath =
        Path.Combine(
            AppContext.BaseDirectory,
            "Data",
            "Schema.sql");

    public static void Initialize()
    {
        using var connection =
            new SqliteConnection($"Data Source={DbPath}");

        connection.Open();

        EnableForeignKeys(connection);

        var schemaSql = File.ReadAllText(SchemaPath);

        using var command = connection.CreateCommand();

        command.CommandText = schemaSql;

        command.ExecuteNonQuery();

        MigrateRecurrenceColumns(connection);

        System.Diagnostics.Debug.WriteLine(DbPath);
    }

    public static SqliteConnection GetConnection()
    {
        var connection =
            new SqliteConnection($"Data Source={DbPath}");

        connection.Open();

        EnableForeignKeys(connection);

        return connection;
    }

    private static void EnableForeignKeys(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText =
            "PRAGMA foreign_keys = ON;";

        command.ExecuteNonQuery();
    }

    // Forward-only schema reconciliation for databases created before the
    // recurrence engine landed. Idempotent — every step is gated on a
    // PRAGMA inspection so re-running on an up-to-date DB is a no-op.
    private static void MigrateRecurrenceColumns(
        SqliteConnection connection)
    {
        var columns = GetEventsColumns(connection);

        if (columns.Contains("RecurrenceRuleJson")
            && !columns.Contains("RecurrenceRule"))
        {
            using var rename = connection.CreateCommand();
            rename.CommandText =
                "ALTER TABLE Events RENAME COLUMN RecurrenceRuleJson TO RecurrenceRule;";
            rename.ExecuteNonQuery();
            columns.Remove("RecurrenceRuleJson");
            columns.Add("RecurrenceRule");
        }

        if (!columns.Contains("RecurrenceExDatesUtc"))
        {
            using var add = connection.CreateCommand();
            add.CommandText =
                "ALTER TABLE Events ADD COLUMN RecurrenceExDatesUtc TEXT;";
            add.ExecuteNonQuery();
        }

        if (!columns.Contains("RecurrenceEndUtcCached"))
        {
            using var add = connection.CreateCommand();
            add.CommandText =
                "ALTER TABLE Events ADD COLUMN RecurrenceEndUtcCached TEXT;";
            add.ExecuteNonQuery();

            using var index = connection.CreateCommand();
            index.CommandText =
                "CREATE INDEX IF NOT EXISTS IX_Events_RecurrenceEndUtcCached "
                + "ON Events(RecurrenceEndUtcCached);";
            index.ExecuteNonQuery();
        }
    }

    private static HashSet<string> GetEventsColumns(
        SqliteConnection connection)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Events);";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
