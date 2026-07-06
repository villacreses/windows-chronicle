using System;
using System.Collections.Generic;
using System.Linq;
using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using Chronicle.Projection;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Projection;

/// <summary>
/// Layer 4 — the projection pipeline extracted from <c>MainWindow</c> into
/// <see cref="EventProjection"/>. Pure (no DB): asserts the wiring that turns
/// repository rows into the day-grouped, visibility-filtered projection the
/// UI renders. Recurrence-expansion internals are Layer 2's job; here we prove
/// standalones pass through, masters are replaced by their occurrences,
/// overrides reach the right series, and visibility filtering groups by day
/// without mutating the source.
/// </summary>
public sealed class EventProjectionTests
{
    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<EventOverride>> NoOverrides =
        new Dictionary<Guid, IReadOnlyList<EventOverride>>();

    private static EventOverride Override(Guid seriesId, DateTime anchorUtc, string? title = null)
        => new EventOverride
        {
            Id = Guid.NewGuid(),
            SeriesEventId = seriesId,
            OccurrenceAnchorUtc = anchorUtc,
            Title = title,
            UpdatedAtUtc = anchorUtc,
        };

    // ── GroupOverridesBySeries ────────────────────────────────────────────

    [Fact]
    public void GroupOverridesBySeries_BucketsBySeriesId()
    {
        var seriesA = Guid.NewGuid();
        var seriesB = Guid.NewGuid();
        var overrides = new List<EventOverride>
        {
            Override(seriesA, Utc(2026, 6, 1, 9, 0), "a1"),
            Override(seriesA, Utc(2026, 6, 8, 9, 0), "a2"),
            Override(seriesB, Utc(2026, 6, 1, 9, 0), "b1"),
        };

        var grouped = EventProjection.GroupOverridesBySeries(overrides);

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped[seriesA].Count);
        Assert.Single(grouped[seriesB]);
    }

    [Fact]
    public void GroupOverridesBySeries_EmptyInput_ReturnsEmptyDictionary()
    {
        Assert.Empty(EventProjection.GroupOverridesBySeries(new List<EventOverride>()));
    }

    // ── ExpandRecurrences ─────────────────────────────────────────────────

    [Fact]
    public void ExpandRecurrences_StandaloneEvents_PassThroughUnchanged()
    {
        var calendarId = NewCalendar().Id;
        var a = StandaloneEvent(calendarId, title: "A");
        var b = StandaloneEvent(calendarId, title: "B");

        var result = EventProjection.ExpandRecurrences(
            new[] { a, b }, Utc(2026, 6, 1), Utc(2026, 6, 30), NoOverrides);

        // Same instances, same order — standalones are not re-projected.
        Assert.Equal(2, result.Count);
        Assert.Same(a, result[0]);
        Assert.Same(b, result[1]);
    }

    [Fact]
    public void ExpandRecurrences_RecurringMaster_ReplacedByOccurrences_MasterAbsent()
    {
        var calendarId = NewCalendar().Id;
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0));

        var result = EventProjection.ExpandRecurrences(
            new[] { master }, Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59), NoOverrides);

        // Weekly with no BYDAY steps +7 days from the start: Jun 1, 8, 15.
        Assert.Equal(
            new[] { Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 8, 9, 0), Utc(2026, 6, 15, 9, 0) },
            result.Select(e => e.SeriesAnchorUtc!.Value));
        // Every result is an occurrence; the master row itself never enters.
        Assert.All(result, e => Assert.True(e.IsOccurrence));
        Assert.All(result, e => Assert.Null(e.RecurrenceRule));
    }

    [Fact]
    public void ExpandRecurrences_MixedRows_StandalonePlusExpansions()
    {
        var calendarId = NewCalendar().Id;
        var standalone = StandaloneEvent(calendarId, title: "One-off");
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0));

        var result = EventProjection.ExpandRecurrences(
            new[] { standalone, master }, Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59), NoOverrides);

        Assert.Contains(standalone, result);
        Assert.Equal(3, result.Count(e => e.IsOccurrence));
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void ExpandRecurrences_OverrideAppliedToRightMaster_OthersUnaffected()
    {
        var calendarId = NewCalendar().Id;
        var seriesA = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0), title: "A");
        var seriesB = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 2, 9, 0), title: "B");

        var grouped = EventProjection.GroupOverridesBySeries(new List<EventOverride>
        {
            Override(seriesA.Id, Utc(2026, 6, 8, 9, 0), title: "A-moved"),
        });

        var result = EventProjection.ExpandRecurrences(
            new[] { seriesA, seriesB }, Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59), grouped);

        // Series A's Jun 8 occurrence carries the override title...
        var overridden = result.Single(
            e => e.Id == seriesA.Id && e.SeriesAnchorUtc == Utc(2026, 6, 8, 9, 0));
        Assert.Equal("A-moved", overridden.Title);
        // ...its other occurrences inherit the master title...
        Assert.Equal("A", result.Single(
            e => e.Id == seriesA.Id && e.SeriesAnchorUtc == Utc(2026, 6, 1, 9, 0)).Title);
        // ...and series B is untouched by A's override.
        Assert.All(result.Where(e => e.Id == seriesB.Id), e => Assert.Equal("B", e.Title));
    }

    // ── GroupVisibleByDay ─────────────────────────────────────────────────

    [Fact]
    public void GroupVisibleByDay_GroupsUnderLocalDayKeys()
    {
        var calendarId = NewCalendar().Id;
        // Two events at the same instant share a day key; a third on another
        // day forms its own group. Keys are computed via DateHelpers so the
        // assertion is timezone-independent.
        var e1 = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 12, 0));
        var e2 = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 12, 0));
        var e3 = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 10, 12, 0));

        var result = EventProjection.GroupVisibleByDay(
            new[] { e1, e2, e3 }, new Dictionary<Guid, bool>());

        var key1 = DateHelpers.GetEventDayKey(e1.StartTimeUtc);
        var key3 = DateHelpers.GetEventDayKey(e3.StartTimeUtc);
        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[key1].Count);
        Assert.Single(result[key3]);
        Assert.All(result.Keys, k => Assert.Equal(DateTimeKind.Local, k.Kind));
    }

    [Fact]
    public void GroupVisibleByDay_HiddenCalendar_FilteredWithoutMutatingSource()
    {
        var visibleCal = NewCalendar();
        var hiddenCal = NewCalendar();
        var shown = StandaloneEvent(visibleCal.Id, title: "Shown");
        var hidden = StandaloneEvent(hiddenCal.Id, title: "Hidden");
        var source = new List<Event> { shown, hidden };

        var visibility = new Dictionary<Guid, bool>
        {
            [visibleCal.Id] = true,
            [hiddenCal.Id] = false,
        };

        var result = EventProjection.GroupVisibleByDay(source, visibility);

        var projected = result.Values.SelectMany(x => x).ToList();
        Assert.Contains(shown, projected);
        Assert.DoesNotContain(hidden, projected);
        // Source list is untouched — filtering does not mutate the projection input.
        Assert.Equal(new[] { shown, hidden }, source);
    }

    [Fact]
    public void GroupVisibleByDay_EmptyVisibilityMap_TreatsAllCalendarsVisible()
    {
        var calA = NewCalendar();
        var calB = NewCalendar();
        var a = StandaloneEvent(calA.Id);
        var b = StandaloneEvent(calB.Id);

        var result = EventProjection.GroupVisibleByDay(
            new[] { a, b }, new Dictionary<Guid, bool>());

        var projected = result.Values.SelectMany(x => x).ToList();
        Assert.Contains(a, projected);
        Assert.Contains(b, projected);
    }

    [Fact]
    public void GroupVisibleByDay_CalendarMissingFromMap_DefaultsVisible()
    {
        var known = NewCalendar();
        var unlisted = NewCalendar();
        var a = StandaloneEvent(known.Id);
        var b = StandaloneEvent(unlisted.Id);

        // Non-empty map that lists only `known`; `unlisted` is absent and must
        // default to visible (matches MainWindow's reconcile-to-visible model).
        var visibility = new Dictionary<Guid, bool> { [known.Id] = true };

        var result = EventProjection.GroupVisibleByDay(new[] { a, b }, visibility);

        var projected = result.Values.SelectMany(x => x).ToList();
        Assert.Contains(a, projected);
        Assert.Contains(b, projected);
    }

    // ── RangeCovered (cache-coverage decision) ────────────────────────────

    [Fact]
    public void RangeCovered_RequestedInsideOrEqualToLoaded_ReturnsTrue()
    {
        var loadedStart = Utc(2026, 6, 1);
        var loadedEnd = Utc(2026, 6, 30);

        // Strictly inside — a Month → Week → Day switch within the loaded span.
        Assert.True(EventProjection.RangeCovered(
            loadedStart, loadedEnd, Utc(2026, 6, 10), Utc(2026, 6, 20)));
        // Exactly equal bounds still count as covered.
        Assert.True(EventProjection.RangeCovered(
            loadedStart, loadedEnd, loadedStart, loadedEnd));
    }

    [Fact]
    public void RangeCovered_RequestedExtendsBeyondLoaded_ReturnsFalse()
    {
        var loadedStart = Utc(2026, 6, 1);
        var loadedEnd = Utc(2026, 6, 30);

        // Starts before the loaded range.
        Assert.False(EventProjection.RangeCovered(
            loadedStart, loadedEnd, Utc(2026, 5, 31), Utc(2026, 6, 20)));
        // Ends after the loaded range.
        Assert.False(EventProjection.RangeCovered(
            loadedStart, loadedEnd, Utc(2026, 6, 10), Utc(2026, 7, 1)));
    }

    [Fact]
    public void RangeCovered_InvalidatedCacheSentinel_NeverCovers()
    {
        // InvalidateLoadedEvents sets loaded = [MaxValue, MinValue] so the next
        // request always misses and reloads.
        Assert.False(EventProjection.RangeCovered(
            DateTime.MaxValue, DateTime.MinValue, Utc(2026, 6, 1), Utc(2026, 6, 30)));
    }

    // ── OrderForDay ───────────────────────────────────────────────────────

    [Fact]
    public void OrderForDay_AllDayFirst_ThenTimedByStart()
    {
        var calendarId = NewCalendar().Id;
        var timedLate = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 15, 0), title: "Late");
        var timedEarly = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 9, 0), title: "Early");
        var allDay = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 1, 0, 0), title: "Holiday", isAllDay: true);

        var ordered = EventProjection.OrderForDay(new[] { timedLate, allDay, timedEarly });

        Assert.Equal(new[] { allDay, timedEarly, timedLate }, ordered);
    }

    [Fact]
    public void OrderForDay_TiesBreakByTitleCaseInsensitive()
    {
        var calendarId = NewCalendar().Id;
        var beta = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 9, 0), title: "beta");
        var alpha = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 9, 0), title: "Alpha");

        var ordered = EventProjection.OrderForDay(new[] { beta, alpha });

        Assert.Equal(new[] { alpha, beta }, ordered);
    }

    [Fact]
    public void OrderForDay_AllDayEventsSortByTitle_NotByStart()
    {
        // All-day events ignore start when ordering among themselves: Apple
        // sorts before Zebra despite Zebra's earlier stored start.
        var calendarId = NewCalendar().Id;
        var zebra = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 1, 3, 0), title: "Zebra", isAllDay: true);
        var apple = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 1, 8, 0), title: "Apple", isAllDay: true);

        var ordered = EventProjection.OrderForDay(new[] { zebra, apple });

        Assert.Equal(new[] { apple, zebra }, ordered);
    }

    [Fact]
    public void GroupVisibleByDay_OrdersEachDay_ViaOrderForDay()
    {
        var calendarId = NewCalendar().Id;
        // Same instant → one day group regardless of the test machine's zone;
        // the all-day event must still sort ahead of the timed one.
        var timed = StandaloneEvent(calendarId, startUtc: Utc(2026, 6, 1, 12, 0), title: "Timed");
        var allDay = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 1, 12, 0), title: "AllDay", isAllDay: true);

        var result = EventProjection.GroupVisibleByDay(
            new[] { timed, allDay }, new Dictionary<Guid, bool>());

        var day = Assert.Single(result.Values);
        Assert.Equal(new[] { allDay, timed }, day);
    }
}
