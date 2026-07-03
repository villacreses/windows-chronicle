using System;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// Field-fidelity round-trips through <see cref="EventRepository"/> insert +
/// <see cref="EventRepository.GetByIdAsync"/>. These guard the storage
/// contract the recurrence engine depends on: EXDATE anchors must survive
/// with full UTC precision (EXDATE matches the projection space bit-for-bit),
/// the anchor <c>TimeZoneId</c> must persist, and nullable recurrence columns
/// must come back null / empty rather than as defaulted junk.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventRepositoryFieldTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    [Fact]
    public async Task ExDates_RoundTrip_WithUtcPrecision()
    {
        var calendarId = await SeedCalendarAsync();
        var exDates = new[]
        {
            Utc(2026, 6, 1, 9, 0),
            // Sub-second precision must survive the ISO-8601 "O" round-trip;
            // an EXDATE that loses ticks would no longer match its anchor.
            Utc(2026, 6, 8, 9, 0).AddTicks(1_234_567),
            Utc(2026, 6, 15, 9, 0),
        };
        var master = RecurringMaster(calendarId, exDates: exDates);

        await _events.InsertAsync(master);
        var loaded = await _events.GetByIdAsync(master.Id);

        Assert.NotNull(loaded);
        Assert.Equal(exDates, loaded!.RecurrenceExDatesUtc);
        // DateTime equality ignores Kind, so assert UTC kind explicitly.
        Assert.All(
            loaded.RecurrenceExDatesUtc,
            d => Assert.Equal(DateTimeKind.Utc, d.Kind));
    }

    [Fact]
    public async Task TimeZoneId_RoundTrips()
    {
        var calendarId = await SeedCalendarAsync();
        var master = RecurringMaster(calendarId, timeZoneId: "America/New_York");

        await _events.InsertAsync(master);
        var loaded = await _events.GetByIdAsync(master.Id);

        Assert.NotNull(loaded);
        Assert.Equal("America/New_York", loaded!.TimeZoneId);
    }

    [Fact]
    public async Task RecurrenceRuleAndCachedEnd_RoundTrip()
    {
        var calendarId = await SeedCalendarAsync();
        var cachedEnd = Utc(2026, 12, 31, 10, 0);
        var master = RecurringMaster(
            calendarId,
            rrule: "FREQ=WEEKLY;COUNT=10",
            endUtcCached: cachedEnd);

        await _events.InsertAsync(master);
        var loaded = await _events.GetByIdAsync(master.Id);

        Assert.NotNull(loaded);
        Assert.Equal("FREQ=WEEKLY;COUNT=10", loaded!.RecurrenceRule);
        Assert.Equal(cachedEnd, loaded.RecurrenceEndUtcCached);
        Assert.Equal(DateTimeKind.Utc, loaded.RecurrenceEndUtcCached!.Value.Kind);
    }

    [Fact]
    public async Task StandaloneEvent_NullableFields_RoundTripAsNullOrEmpty()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = StandaloneEvent(calendarId); // no description, no recurrence

        await _events.InsertAsync(evt);
        var loaded = await _events.GetByIdAsync(evt.Id);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.Description);
        Assert.Null(loaded.RecurrenceRule);
        Assert.Empty(loaded.RecurrenceExDatesUtc);
        Assert.Null(loaded.RecurrenceEndUtcCached);
        Assert.Null(loaded.TimeZoneId);
        Assert.False(loaded.IsRecurring);
        Assert.False(loaded.IsOccurrence);
    }
}
