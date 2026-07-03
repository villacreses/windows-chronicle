using System;
using System.IO;
using Chronicle.Data;
using Microsoft.Data.Sqlite;

namespace Chronicle.Tests.Data;

/// <summary>
/// Groups every test that touches <see cref="AppDatabase"/> into a single
/// non-parallel collection. <c>AppDatabase</c> holds the database path in
/// static state, and xUnit parallelizes test classes by default — without
/// serializing them, two DB test classes running at once would stomp each
/// other's path. See <c>.context/TESTING.md</c> "When Layer 3 lands".
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseCollection
{
    public const string Name = "Database";
}

/// <summary>
/// Base for tests that own an isolated on-disk SQLite database. The path is
/// unique per test (xUnit constructs a fresh instance per <c>[Fact]</c>);
/// teardown clears the Microsoft.Data.Sqlite connection pool — which keeps
/// the file open — so the temp database can be deleted.
///
/// This base deliberately does NOT call <see cref="AppDatabase.Initialize"/>:
/// migration tests need to stage a pre-migration schema before initializing.
/// Repository tests that just want the standard schema derive from
/// <see cref="InitializedDatabaseTest"/> instead.
///
/// Concrete test classes must carry <c>[Collection(DatabaseCollection.Name)]</c>
/// — xUnit does not honor the attribute through inheritance.
/// </summary>
public abstract class DatabaseTest : IDisposable
{
    protected string DbPath { get; }

    protected DatabaseTest()
    {
        DbPath = Path.Combine(
            Path.GetTempPath(),
            $"chronicle-test-{Guid.NewGuid():N}.db");
    }

    protected void InitializeDatabase() => AppDatabase.Initialize(DbPath);

    public virtual void Dispose()
    {
        // Pooling keeps the file handle open; clear it so Delete succeeds.
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(DbPath))
                File.Delete(DbPath);
        }
        catch (IOException)
        {
            // A still-held handle only leaks a temp file — not worth failing
            // the run over.
        }
    }
}

/// <summary>
/// Base for repository tests: initializes the standard schema (<c>Schema.sql</c>
/// plus the recurrence migration) before the test body runs, so the test can
/// go straight to exercising repositories against an isolated database.
/// </summary>
public abstract class InitializedDatabaseTest : DatabaseTest
{
    protected InitializedDatabaseTest()
    {
        InitializeDatabase();
    }
}
