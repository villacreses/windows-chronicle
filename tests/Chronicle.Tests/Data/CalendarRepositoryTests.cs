using System;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// CRUD, the "always at least one calendar" invariant
/// (<see cref="CalendarRepository.EnsureDefaultAsync"/>), and the
/// cascade-delete contract (a calendar delete removes its events and their
/// overrides in one transaction) for <see cref="CalendarRepository"/>.
///
/// Database isolation and teardown come from <see cref="InitializedDatabaseTest"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CalendarRepositoryTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly OverrideRepository _overrides = new();

    [Fact]
    public async Task Insert_ThenGetAll_ContainsCalendar()
    {
        var calendar = NewCalendar("Work", "#123456");

        await _calendars.InsertAsync(calendar);

        var loaded = Assert.Single(await _calendars.GetAllAsync());
        Assert.Equal(calendar.Id, loaded.Id);
        Assert.Equal("Work", loaded.Name);
        Assert.Equal("#123456", loaded.Color);
    }

    [Fact]
    public async Task Update_ChangesNameAndColor()
    {
        var calendar = NewCalendar("Work", "#123456");
        await _calendars.InsertAsync(calendar);

        calendar.Name = "Personal";
        calendar.Color = "#ABCDEF";
        await _calendars.UpdateAsync(calendar);

        var loaded = Assert.Single(await _calendars.GetAllAsync());
        Assert.Equal("Personal", loaded.Name);
        Assert.Equal("#ABCDEF", loaded.Color);
    }

    [Fact]
    public async Task Delete_RemovesCalendar()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);

        await _calendars.DeleteAsync(calendar.Id);

        Assert.Empty(await _calendars.GetAllAsync());
    }

    [Fact]
    public async Task Delete_CascadesEventsAndOverrides_LeavingOtherCalendarsIntact()
    {
        // Target calendar: a recurring master with an override, plus a
        // standalone event.
        var target = NewCalendar("Target");
        await _calendars.InsertAsync(target);

        var master = RecurringMaster(target.Id, startUtc: Utc(2026, 6, 1, 9, 0));
        await _events.InsertAsync(master);
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "Moved"));
        var standalone = StandaloneEvent(target.Id);
        await _events.InsertAsync(standalone);

        // Bystander calendar with its own event that must survive.
        var bystander = NewCalendar("Bystander");
        await _calendars.InsertAsync(bystander);
        var survivor = StandaloneEvent(bystander.Id, title: "Survivor");
        await _events.InsertAsync(survivor);

        await _calendars.DeleteAsync(target.Id);

        // Calendar and both of its events are gone; the series' overrides too.
        Assert.DoesNotContain(
            await _calendars.GetAllAsync(), c => c.Id == target.Id);
        Assert.Null(await _events.GetByIdAsync(master.Id));
        Assert.Null(await _events.GetByIdAsync(standalone.Id));
        Assert.Empty(await _overrides.GetForSeriesAsync(master.Id));

        // The bystander calendar and its event are untouched.
        Assert.Contains(
            await _calendars.GetAllAsync(), c => c.Id == bystander.Id);
        Assert.NotNull(await _events.GetByIdAsync(survivor.Id));
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
