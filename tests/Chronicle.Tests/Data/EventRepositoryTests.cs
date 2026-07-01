using System;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// CRUD, the persistence-boundary occurrence guard, and override cascade
/// for <see cref="EventRepository"/> against an isolated SQLite database.
/// Field-fidelity round-trips (EXDATE, TimeZoneId) and range-query shape
/// are covered separately.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventRepositoryTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly OverrideRepository _overrides = new();

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    [Fact]
    public async Task Insert_ThenGetById_RoundTripsCoreFields()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = StandaloneEvent(
            calendarId,
            startUtc: Utc(2026, 6, 1, 9, 0),
            duration: TimeSpan.FromHours(2),
            title: "Standup",
            description: "Daily sync",
            isAllDay: false);

        await _events.InsertAsync(evt);

        var loaded = await _events.GetByIdAsync(evt.Id);

        Assert.NotNull(loaded);
        Assert.Equal(evt.Id, loaded!.Id);
        Assert.Equal(calendarId, loaded.CalendarId);
        Assert.Equal("Standup", loaded.Title);
        Assert.Equal("Daily sync", loaded.Description);
        Assert.Equal(evt.StartTimeUtc, loaded.StartTimeUtc);
        Assert.Equal(evt.EndTimeUtc, loaded.EndTimeUtc);
        Assert.False(loaded.IsAllDay);
        // Persisted timestamps come back as UTC-kind, not Unspecified.
        Assert.Equal(DateTimeKind.Utc, loaded.StartTimeUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.EndTimeUtc.Kind);
    }

    [Fact]
    public async Task Update_ChangesPersistedFields()
    {
        var calendarId = await SeedCalendarAsync();
        var otherCalendar = NewCalendar("Other");
        await _calendars.InsertAsync(otherCalendar);

        var evt = StandaloneEvent(calendarId, title: "Before");
        await _events.InsertAsync(evt);

        evt.Title = "After";
        evt.Description = "Now with notes";
        evt.CalendarId = otherCalendar.Id;
        evt.StartTimeUtc = Utc(2026, 7, 1, 10, 0);
        evt.EndTimeUtc = Utc(2026, 7, 1, 11, 0);
        evt.IsAllDay = true;
        await _events.UpdateAsync(evt);

        var loaded = await _events.GetByIdAsync(evt.Id);

        Assert.NotNull(loaded);
        Assert.Equal("After", loaded!.Title);
        Assert.Equal("Now with notes", loaded.Description);
        Assert.Equal(otherCalendar.Id, loaded.CalendarId);
        Assert.Equal(Utc(2026, 7, 1, 10, 0), loaded.StartTimeUtc);
        Assert.Equal(Utc(2026, 7, 1, 11, 0), loaded.EndTimeUtc);
        Assert.True(loaded.IsAllDay);
    }

    [Fact]
    public async Task Delete_RemovesEvent()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = StandaloneEvent(calendarId);
        await _events.InsertAsync(evt);

        await _events.DeleteAsync(evt.Id);

        Assert.Null(await _events.GetByIdAsync(evt.Id));
    }

    [Fact]
    public async Task GetByIdAsync_MissingRow_ReturnsNull()
    {
        Assert.Null(await _events.GetByIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Insert_RefusesExpandedOccurrence()
    {
        var calendarId = await SeedCalendarAsync();
        var occurrence = StandaloneEvent(calendarId);
        // SeriesAnchorUtc set => IsOccurrence true. The repository writes
        // persistent rows only; occurrences are projections.
        occurrence.SeriesAnchorUtc = Utc(2026, 6, 1, 9, 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _events.InsertAsync(occurrence));
        Assert.Contains("occurrence", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_RefusesExpandedOccurrence()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = StandaloneEvent(calendarId);
        await _events.InsertAsync(evt);

        // Same row, now carrying an anchor — must be refused at the boundary
        // rather than overwriting the master with a projected occurrence.
        evt.SeriesAnchorUtc = Utc(2026, 6, 1, 9, 0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _events.UpdateAsync(evt));
    }

    [Fact]
    public async Task Delete_CascadesOverrides()
    {
        var calendarId = await SeedCalendarAsync();
        var master = RecurringMaster(calendarId, startUtc: Utc(2026, 6, 1, 9, 0));
        await _events.InsertAsync(master);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "Moved"));
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 8, 9, 0)),
            new OverrideFields(Title: "Also moved"));

        await _events.DeleteAsync(master.Id);

        Assert.Null(await _events.GetByIdAsync(master.Id));
        Assert.Empty(await _overrides.GetForSeriesAsync(master.Id));
    }
}
