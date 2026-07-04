using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using Chronicle.Projection;
using Chronicle.Tests.Data;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Integration;

/// <summary>
/// Integration coverage for the load pipeline that <c>MainWindow</c> runs in
/// <c>EnsureEventsLoadedAsync</c> + <c>ApplyVisibilityFilter</c>: query the
/// repository for a range, collect recurring master ids, bulk-fetch their
/// overrides, then hand everything to <see cref="EventProjection"/> to expand,
/// merge, and group by visible day.
///
/// The Layer 3 and Layer 4 unit tests each prove one link with hand-built
/// objects; these prove the links compose against <b>real SQLite</b> — EXDATE
/// and override rows survive serialization and then actually merge onto the
/// masters they were expanded from, hidden calendars drop after the load, and
/// finite series pruned by the range query never reach the expander.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventPipelineTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly OverrideRepository _overrides = new();

    // Mirrors MainWindow.EnsureEventsLoadedAsync + ApplyVisibilityFilter: the
    // interleaved repository reads plus the EventProjection composition.
    private async Task<Dictionary<DateTime, List<Event>>> LoadProjectionAsync(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyDictionary<Guid, bool> visibility)
    {
        var rows = await _events.GetInRangeAsync(rangeStartUtc, rangeEndUtc);

        var recurringMasterIds = rows
            .Where(r => r.RecurrenceRule is not null)
            .Select(r => r.Id)
            .ToList();

        var overrides = recurringMasterIds.Count == 0
            ? new List<EventOverride>()
            : await _overrides.GetForSeriesAsync(recurringMasterIds);

        var overridesBySeries = EventProjection.GroupOverridesBySeries(overrides);
        var projected = EventProjection.ExpandRecurrences(
            rows, rangeStartUtc, rangeEndUtc, overridesBySeries);
        return EventProjection.GroupVisibleByDay(projected, visibility);
    }

    private static List<Event> Flatten(Dictionary<DateTime, List<Event>> byDay)
        => byDay.Values.SelectMany(x => x).ToList();

    private async Task<Guid> SeedCalendarAsync(string name = "Cal")
    {
        var calendar = NewCalendar(name);
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    [Fact]
    public async Task Pipeline_StandaloneRecurringOverrideExdateAndHiddenCalendar_ComposeCorrectly()
    {
        var visibleCal = await SeedCalendarAsync("Visible");
        var hiddenCal = await SeedCalendarAsync("Hidden");

        var standalone = StandaloneEvent(
            visibleCal, startUtc: Utc(2026, 6, 10, 12, 0), title: "Standalone");
        await _events.InsertAsync(standalone);

        // Weekly series with Jun 15 excluded via EXDATE (round-trips through
        // serialization) and an override retitling Jun 8.
        var master = RecurringMaster(
            visibleCal,
            rrule: "FREQ=WEEKLY",
            startUtc: Utc(2026, 6, 1, 9, 0),
            exDates: new[] { Utc(2026, 6, 15, 9, 0) },
            title: "Series");
        await _events.InsertAsync(master);
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 8, 9, 0)),
            new OverrideFields(Title: "Moved standup"));

        // An event on a calendar the user has hidden — loaded by the query,
        // dropped by the visibility filter.
        var hiddenEvent = StandaloneEvent(
            hiddenCal, startUtc: Utc(2026, 6, 12, 12, 0), title: "Hidden");
        await _events.InsertAsync(hiddenEvent);

        var visibility = new Dictionary<Guid, bool>
        {
            [visibleCal] = true,
            [hiddenCal] = false,
        };

        var result = await LoadProjectionAsync(
            Utc(2026, 6, 1), Utc(2026, 6, 28, 23, 59), visibility);
        var all = Flatten(result);

        // Hidden calendar removed; no master row leaks as itself.
        Assert.DoesNotContain(all, e => e.CalendarId == hiddenCal);
        Assert.All(all, e => Assert.Null(e.RecurrenceRule));

        // Standalone present and grouped under its local day key.
        var standaloneKey = DateHelpers.GetEventDayKey(standalone.StartTimeUtc);
        Assert.Contains(result[standaloneKey], e => e.Title == "Standalone");

        // Weekly occurrences: Jun 1, 8, 22 — Jun 15 removed by the persisted EXDATE.
        var occurrences = all
            .Where(e => e.Id == master.Id)
            .OrderBy(e => e.SeriesAnchorUtc)
            .ToList();
        Assert.Equal(
            new[] { Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 8, 9, 0), Utc(2026, 6, 22, 9, 0) },
            occurrences.Select(e => e.SeriesAnchorUtc!.Value));

        // The persisted override merged onto its occurrence; siblings inherit.
        Assert.Equal("Moved standup",
            occurrences.Single(e => e.SeriesAnchorUtc == Utc(2026, 6, 8, 9, 0)).Title);
        Assert.Equal("Series",
            occurrences.Single(e => e.SeriesAnchorUtc == Utc(2026, 6, 1, 9, 0)).Title);
    }

    [Fact]
    public async Task Pipeline_FiniteSeriesEndedBeforeRange_IsPrunedAtQuery_LiveSeriesExpands()
    {
        var calendar = await SeedCalendarAsync();

        // Cached end precedes the range → GetInRangeAsync never returns it, so
        // the expander is never handed a dead series.
        var ended = RecurringMaster(
            calendar, rrule: "FREQ=WEEKLY;COUNT=2", startUtc: Utc(2026, 5, 1, 9, 0),
            endUtcCached: Utc(2026, 5, 8, 9, 30));
        await _events.InsertAsync(ended);

        var live = RecurringMaster(
            calendar, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0));
        await _events.InsertAsync(live);

        var result = await LoadProjectionAsync(
            Utc(2026, 6, 10), Utc(2026, 6, 20, 23, 59), new Dictionary<Guid, bool>());
        var all = Flatten(result);

        Assert.DoesNotContain(all, e => e.Id == ended.Id);

        // The live weekly series contributes only its in-range occurrence (Jun 15).
        var liveOccurrences = all.Where(e => e.Id == live.Id).ToList();
        Assert.Single(liveOccurrences);
        Assert.Equal(Utc(2026, 6, 15, 9, 0), liveOccurrences[0].SeriesAnchorUtc);
    }

    [Fact]
    public async Task Pipeline_StandalonesOnly_SkipsOverrideFetch_AndGroupsByDay()
    {
        var calendar = await SeedCalendarAsync();

        var day1 = StandaloneEvent(calendar, startUtc: Utc(2026, 6, 10, 9, 0), title: "One");
        var day2 = StandaloneEvent(calendar, startUtc: Utc(2026, 6, 12, 9, 0), title: "Two");
        await _events.InsertAsync(day1);
        await _events.InsertAsync(day2);

        // No recurring masters → the override bulk-fetch branch is skipped.
        var result = await LoadProjectionAsync(
            Utc(2026, 6, 1), Utc(2026, 6, 28, 23, 59), new Dictionary<Guid, bool>());

        Assert.Equal(2, Flatten(result).Count);
        Assert.Contains(result[DateHelpers.GetEventDayKey(day1.StartTimeUtc)], e => e.Title == "One");
        Assert.Contains(result[DateHelpers.GetEventDayKey(day2.StartTimeUtc)], e => e.Title == "Two");
    }
}
