using System;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;

namespace Chronicle.Tests.Data;

/// <summary>
/// Covers the "always at least one calendar" invariant enforced by
/// <see cref="CalendarRepository.EnsureDefaultAsync"/>: a fresh database
/// self-heals with a single "Default" calendar, and the call is a no-op
/// once any calendar exists — so it is safe to run on every startup and
/// after every calendar delete.
///
/// Database isolation and teardown come from <see cref="InitializedDatabaseTest"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CalendarRepositoryTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();

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
