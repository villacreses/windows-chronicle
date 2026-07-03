using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// Query-shape contract for <see cref="EventRepository.GetInRangeAsync"/>.
/// The method returns candidate ROWS — standalone events that overlap the
/// window, and recurring masters that could still project an occurrence into
/// it — never expanded occurrences (expansion happens later in the pipeline).
/// Finite series whose cached end precedes the window are pruned so the
/// expander is not handed dead series.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventRepositoryRangeTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();

    // A fixed 10-day window; every case is positioned relative to it.
    private static readonly DateTime RangeStart = Utc(2026, 6, 10, 0, 0);
    private static readonly DateTime RangeEnd = Utc(2026, 6, 20, 0, 0);

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    private async Task<HashSet<Guid>> IdsInRangeAsync()
    {
        var rows = await _events.GetInRangeAsync(RangeStart, RangeEnd);
        return rows.Select(e => e.Id).ToHashSet();
    }

    [Fact]
    public async Task IncludesOverlappingStandalones_ExcludesEventsOutsideWindow()
    {
        var calendarId = await SeedCalendarAsync();

        var inside = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 12, 9, 0));
        // End lands exactly on RangeStart — inclusive lower bound.
        var touchingStart = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 9, 23, 0), duration: TimeSpan.FromHours(1));
        // Start lands exactly on RangeEnd — inclusive upper bound.
        var touchingEnd = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 20, 0, 0));
        var before = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 9, 0));
        var after = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 25, 9, 0));

        foreach (var e in new[] { inside, touchingStart, touchingEnd, before, after })
            await _events.InsertAsync(e);

        var ids = await IdsInRangeAsync();

        Assert.Contains(inside.Id, ids);
        Assert.Contains(touchingStart.Id, ids);
        Assert.Contains(touchingEnd.Id, ids);
        Assert.DoesNotContain(before.Id, ids);
        Assert.DoesNotContain(after.Id, ids);
    }

    [Fact]
    public async Task IncludesRecurringMasters_ThatMayProduceOccurrencesInRange()
    {
        var calendarId = await SeedCalendarAsync();

        // Infinite series that began well before the window: no cached end,
        // starts on or before RangeEnd → still a candidate.
        var infinite = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0),
            endUtcCached: null);
        // Finite series whose cached end sits inside the window → candidate.
        var finiteEndingInRange = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY;COUNT=5", startUtc: Utc(2026, 6, 1, 9, 0),
            endUtcCached: Utc(2026, 6, 15, 9, 0));

        await _events.InsertAsync(infinite);
        await _events.InsertAsync(finiteEndingInRange);

        var ids = await IdsInRangeAsync();

        Assert.Contains(infinite.Id, ids);
        Assert.Contains(finiteEndingInRange.Id, ids);
    }

    [Fact]
    public async Task PrunesFiniteSeriesEndedBeforeRange_AndMastersStartingAfterRange()
    {
        var calendarId = await SeedCalendarAsync();

        // Cached end precedes RangeStart → the series is dead in this window.
        var ended = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY;COUNT=2", startUtc: Utc(2026, 6, 1, 9, 0),
            endUtcCached: Utc(2026, 6, 5, 9, 0));
        // Master starts after RangeEnd → no occurrence can fall in the window.
        var startsAfter = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 25, 9, 0),
            endUtcCached: null);

        await _events.InsertAsync(ended);
        await _events.InsertAsync(startsAfter);

        var ids = await IdsInRangeAsync();

        Assert.DoesNotContain(ended.Id, ids);
        Assert.DoesNotContain(startsAfter.Id, ids);
    }
}
