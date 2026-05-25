using Microsoft.Data.Sqlite;
using System;
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
}