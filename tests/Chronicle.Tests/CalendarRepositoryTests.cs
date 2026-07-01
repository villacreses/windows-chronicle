using System;
using System.IO;
using System.Threading.Tasks;
using Chronicle.Data;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using Microsoft.Data.Sqlite;

namespace Chronicle.Tests;

/// <summary>
/// Covers the "always at least one calendar" invariant enforced by
/// <see cref="CalendarRepository.EnsureDefaultAsync"/>: a fresh database
/// self-heals with a single "Default" calendar, and the call is a no-op
/// once any calendar exists — so it is safe to run on every startup and
/// after every calendar delete.
/// </summary>
public sealed class CalendarRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly CalendarRepository _calendars = new();

    public CalendarRepositoryTests()
    {
        // Isolated on-disk database, one per test (xUnit constructs a fresh
        // instance per [Fact], and tests within a class run sequentially, so
        // the static AppDatabase path never clashes). Initialize(dbPath) is
        // the domain's test seam — no Windows.Storage path is resolved here.
        _dbPath = Path.Combine(
            Path.GetTempPath(),
            $"chronicle-test-{Guid.NewGuid():N}.db");

        AppDatabase.Initialize(_dbPath);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections, which keeps the file open;
        // clear the pool so the temp database can be deleted.
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
                File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // A still-held handle only leaks a temp file — not worth failing
            // the run over.
        }
    }

    [Fact]
    public async Task EnsureDefaultAsync_OnEmptyDatabase_CreatesSingleDefaultCalendar()
    {
        await _calendars.EnsureDefaultAsync();

        var all = await _calendars.GetAllAsync();

        var calendar = Assert.Single(all);
        Assert.Equal("Default", calendar.Name);
        Assert.Equal(Calendar.DefaultColorHex, calendar.Color);
    }

    [Fact]
    public async Task EnsureDefaultAsync_WhenACalendarAlreadyExists_IsNoOp()
    {
        await _calendars.InsertAsync(
            new Calendar { Id = Guid.NewGuid(), Name = "Work" });

        await _calendars.EnsureDefaultAsync();

        var all = await _calendars.GetAllAsync();

        var calendar = Assert.Single(all);
        Assert.Equal("Work", calendar.Name);
    }

    [Fact]
    public async Task EnsureDefaultAsync_CalledRepeatedly_CreatesOnlyOneCalendar()
    {
        await _calendars.EnsureDefaultAsync();
        await _calendars.EnsureDefaultAsync();

        var all = await _calendars.GetAllAsync();

        Assert.Single(all);
    }
}
