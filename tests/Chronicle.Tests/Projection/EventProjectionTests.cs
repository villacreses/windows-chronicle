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

    // ── SearchOccurrences ─────────────────────────────────────────────────

    [Fact]
    public void SearchOccurrences_EmptyQuery_ReturnsEmpty()
    {
        var calendarId = NewCalendar().Id;
        var e = StandaloneEvent(calendarId, title: "Anything");

        Assert.Empty(EventProjection.SearchOccurrences(
            new[] { e }, NoOverrides, "", Utc(2026, 6, 1), Utc(2026, 6, 30)));
        Assert.Empty(EventProjection.SearchOccurrences(
            new[] { e }, NoOverrides, "   ", Utc(2026, 6, 1), Utc(2026, 6, 30)));
    }

    [Fact]
    public void SearchOccurrences_Standalones_TitleOrDescriptionMatch_Others_Excluded()
    {
        var calendarId = NewCalendar().Id;
        var titleHit = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 5, 9, 0), title: "Standup");
        var descHit = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 6, 9, 0),
            title: "Sync", description: "Weekly team STANDUP notes");
        var miss = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 7, 9, 0), title: "Lunch");

        var result = EventProjection.SearchOccurrences(
            new[] { titleHit, descHit, miss }, NoOverrides,
            "standup", Utc(2026, 6, 1), Utc(2026, 6, 30));

        Assert.Equal(new[] { titleHit, descHit }, result);
    }

    [Fact]
    public void SearchOccurrences_RecurringMaster_MasterTitleMatches_AllOccurrencesReturned()
    {
        var calendarId = NewCalendar().Id;
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0),
            title: "Standup");

        var result = EventProjection.SearchOccurrences(
            new[] { master }, NoOverrides,
            "standup", Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59));

        Assert.Equal(3, result.Count);
        Assert.All(result, e => Assert.True(e.IsOccurrence));
        Assert.All(result, e => Assert.Equal("Standup", e.Title));
    }

    [Fact]
    public void SearchOccurrences_OverrideRenamesToMatch_OnlyThatOccurrenceReturned()
    {
        // The classic "override-only match" case: master title doesn't hit,
        // but occurrence #2 was renamed via override. Only that occurrence
        // should surface.
        var calendarId = NewCalendar().Id;
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0),
            title: "Sync");

        var grouped = EventProjection.GroupOverridesBySeries(new List<EventOverride>
        {
            Override(master.Id, Utc(2026, 6, 8, 9, 0), title: "Lunch meeting"),
        });

        var result = EventProjection.SearchOccurrences(
            new[] { master }, grouped,
            "lunch", Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59));

        var only = Assert.Single(result);
        Assert.Equal(Utc(2026, 6, 8, 9, 0), only.SeriesAnchorUtc);
        Assert.Equal("Lunch meeting", only.Title);
    }

    [Fact]
    public void SearchOccurrences_OverrideRenamesAwayFromMatch_ThatOccurrenceExcluded()
    {
        // The reverse "divergence" case: master title matches, so every
        // occurrence inherits and matches — except the one an override
        // renamed away. That single occurrence must be excluded even
        // though the SQL candidate step returned the master.
        var calendarId = NewCalendar().Id;
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0),
            title: "Standup");

        var grouped = EventProjection.GroupOverridesBySeries(new List<EventOverride>
        {
            Override(master.Id, Utc(2026, 6, 8, 9, 0), title: "Solo coding time"),
        });

        var result = EventProjection.SearchOccurrences(
            new[] { master }, grouped,
            "standup", Utc(2026, 6, 1), Utc(2026, 6, 21, 23, 59));

        Assert.Equal(
            new[] { Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 15, 9, 0) },
            result.Select(e => e.SeriesAnchorUtc!.Value));
        Assert.DoesNotContain(result, e => e.SeriesAnchorUtc == Utc(2026, 6, 8, 9, 0));
    }

    [Fact]
    public void SearchOccurrences_CaseInsensitiveMatch_OnBothFields()
    {
        var calendarId = NewCalendar().Id;
        var mixedCaseTitle = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 5, 9, 0), title: "PROJECT Kickoff");
        var mixedCaseDesc = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 6, 9, 0),
            title: "Sync", description: "notes on the PROJECT");

        var result = EventProjection.SearchOccurrences(
            new[] { mixedCaseTitle, mixedCaseDesc }, NoOverrides,
            "project", Utc(2026, 6, 1), Utc(2026, 6, 30));

        Assert.Equal(new[] { mixedCaseTitle, mixedCaseDesc }, result);
    }

    [Fact]
    public void SearchOccurrences_OrdersResultsChronologicallyByStart_AcrossMastersAndStandalones()
    {
        var calendarId = NewCalendar().Id;
        var laterStandalone = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 20, 9, 0), title: "Standup review");
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 6, 1, 9, 0),
            title: "Standup");

        var result = EventProjection.SearchOccurrences(
            // Deliberately pass in reverse order: results must still come out
            // sorted by StartTimeUtc regardless of input order.
            new[] { laterStandalone, master }, NoOverrides,
            "standup", Utc(2026, 6, 1), Utc(2026, 6, 20, 23, 59));

        Assert.Equal(
            new[]
            {
                Utc(2026, 6, 1, 9, 0),
                Utc(2026, 6, 8, 9, 0),
                Utc(2026, 6, 15, 9, 0),
                Utc(2026, 6, 20, 9, 0),
            },
            result.Select(e => e.StartTimeUtc));
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

    // ── GroupRemindersByEvent / ReminderSchedule ──────────────────────────

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<Reminder>> NoReminders =
        new Dictionary<Guid, IReadOnlyList<Reminder>>();

    private static Reminder ReminderFor(
        Guid eventId, int quantity, ReminderOffsetUnit unit) => new()
    {
        Id = Guid.NewGuid(),
        EventId = eventId,
        OffsetQuantity = quantity,
        OffsetUnit = unit,
    };

    [Fact]
    public void GroupRemindersByEvent_BucketsByEventId()
    {
        var eventA = Guid.NewGuid();
        var eventB = Guid.NewGuid();
        var grouped = EventProjection.GroupRemindersByEvent(new List<Reminder>
        {
            ReminderFor(eventA, 10, ReminderOffsetUnit.Minutes),
            ReminderFor(eventA, 1, ReminderOffsetUnit.Days),
            ReminderFor(eventB, 1, ReminderOffsetUnit.Hours),
        });

        Assert.Equal(2, grouped.Count);
        Assert.Equal(2, grouped[eventA].Count);
        Assert.Single(grouped[eventB]);
    }

    [Fact]
    public void ReminderSchedule_DerivesFireTime_FromStoredQuantityAndUnit()
    {
        var calendarId = NewCalendar().Id;
        var evt = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 20, 9, 0), title: "Dentist");
        var reminders = EventProjection.GroupRemindersByEvent(new List<Reminder>
        {
            // "2 weeks before" — stored as (2, Weeks), minutes derived.
            ReminderFor(evt.Id, 2, ReminderOffsetUnit.Weeks),
        });

        var result = EventProjection.ReminderSchedule(
            new[] { evt }, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1));

        var only = Assert.Single(result);
        Assert.Equal(Utc(2026, 7, 6, 9, 0), only.FireTimeUtc); // 14 days before
        Assert.Equal(Utc(2026, 7, 20, 9, 0), only.EventStartTimeUtc);
        Assert.Equal("Dentist", only.Title);
    }

    [Fact]
    public void ReminderSchedule_Standalone_KeyedByMasterRef_AndReminderId()
    {
        var calendarId = NewCalendar().Id;
        var evt = StandaloneEvent(calendarId, startUtc: Utc(2026, 7, 20, 9, 0));
        var reminder = ReminderFor(evt.Id, 10, ReminderOffsetUnit.Minutes);
        var reminders = EventProjection.GroupRemindersByEvent(
            new List<Reminder> { reminder });

        var only = Assert.Single(EventProjection.ReminderSchedule(
            new[] { evt }, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1)));

        var master = Assert.IsType<EventRef.Master>(only.Ref);
        Assert.Equal(evt.Id, master.Id);
        Assert.Equal(reminder.Id, only.ReminderId);
    }

    [Fact]
    public void ReminderSchedule_EventsWithoutReminders_Skipped()
    {
        var calendarId = NewCalendar().Id;
        var with = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 10, 9, 0), title: "Has");
        var without = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 11, 9, 0), title: "None");
        var reminders = EventProjection.GroupRemindersByEvent(new List<Reminder>
        {
            ReminderFor(with.Id, 10, ReminderOffsetUnit.Minutes),
        });

        var result = EventProjection.ReminderSchedule(
            new[] { with, without }, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1));

        Assert.Single(result);
        Assert.Equal("Has", result[0].Title);
    }

    [Fact]
    public void ReminderSchedule_FiltersFireTimesOutsideWindow()
    {
        var calendarId = NewCalendar().Id;
        // Fires Jul 10 08:45 — inside [Jul 1, Jul 31].
        var inside = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 10, 9, 0), title: "Inside");
        // Fires Jun 30 23:50 — before the window opens.
        var before = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 1, 0, 0), title: "Before");
        // Fires Aug 1 08:45 — after the window closes.
        var after = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 8, 1, 9, 0), title: "After");
        var reminders = EventProjection.GroupRemindersByEvent(new List<Reminder>
        {
            ReminderFor(inside.Id, 15, ReminderOffsetUnit.Minutes),
            ReminderFor(before.Id, 10, ReminderOffsetUnit.Minutes),
            ReminderFor(after.Id, 15, ReminderOffsetUnit.Minutes),
        });

        var result = EventProjection.ReminderSchedule(
            new[] { inside, before, after }, reminders,
            Utc(2026, 7, 1), Utc(2026, 7, 31));

        Assert.Single(result);
        Assert.Equal("Inside", result[0].Title);
    }

    [Fact]
    public void ReminderSchedule_RecurringOccurrences_InheritViaEventIdJoin_NoExpanderInvolvement()
    {
        // The inheritance mechanism IS the identity contract: an expanded
        // occurrence carries its master's Id, so the EventId-keyed lookup
        // hands every occurrence the master's reminders with no reminder
        // code in the expander. Each fires relative to its own start.
        var calendarId = NewCalendar().Id;
        var master = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY", startUtc: Utc(2026, 7, 6, 9, 0),
            title: "Standup");
        var reminder = ReminderFor(master.Id, 30, ReminderOffsetUnit.Minutes);
        var reminders = EventProjection.GroupRemindersByEvent(
            new List<Reminder> { reminder });

        var expanded = EventProjection.ExpandRecurrences(
            new[] { master }, Utc(2026, 7, 6), Utc(2026, 7, 26, 23, 59), NoOverrides);
        var result = EventProjection.ReminderSchedule(
            expanded, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1));

        // Jul 6, 13, 20 at 09:00 → reminders at 08:30 each.
        Assert.Equal(
            new[] { Utc(2026, 7, 6, 8, 30), Utc(2026, 7, 13, 8, 30), Utc(2026, 7, 20, 8, 30) },
            result.Select(r => r.FireTimeUtc));

        // Each intent keys to its own occurrence (same series id,
        // discriminated by anchor) and to the shared reminder's id.
        Assert.All(result, r =>
        {
            var occ = Assert.IsType<EventRef.Occurrence>(r.Ref);
            Assert.Equal(master.Id, occ.SeriesId);
            Assert.Equal(reminder.Id, r.ReminderId);
        });
        Assert.Equal(
            new[] { Utc(2026, 7, 6, 9, 0), Utc(2026, 7, 13, 9, 0), Utc(2026, 7, 20, 9, 0) },
            result.Select(r => ((EventRef.Occurrence)r.Ref).AnchorUtc));
    }

    [Fact]
    public void ReminderSchedule_MultipleRemindersPerEvent_EmitDistinctIntents()
    {
        // The domain supports N reminders even though the MVP editor shows
        // one. Two reminders on one event → two intents on the same
        // occurrence, discriminated by ReminderId.
        var calendarId = NewCalendar().Id;
        var evt = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 20, 9, 0), title: "Flight");
        var dayBefore = ReminderFor(evt.Id, 1, ReminderOffsetUnit.Days);
        var tenMinutes = ReminderFor(evt.Id, 10, ReminderOffsetUnit.Minutes);
        var reminders = EventProjection.GroupRemindersByEvent(
            new List<Reminder> { dayBefore, tenMinutes });

        var result = EventProjection.ReminderSchedule(
            new[] { evt }, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1));

        Assert.Equal(2, result.Count);
        // Ordered by fire time: the day-before intent fires first.
        Assert.Equal(Utc(2026, 7, 19, 9, 0), result[0].FireTimeUtc);
        Assert.Equal(dayBefore.Id, result[0].ReminderId);
        Assert.Equal(Utc(2026, 7, 20, 8, 50), result[1].FireTimeUtc);
        Assert.Equal(tenMinutes.Id, result[1].ReminderId);
        // Same occurrence identity on both.
        Assert.All(result, r => Assert.Equal(
            evt.Id, Assert.IsType<EventRef.Master>(r.Ref).Id));
    }

    [Fact]
    public void ReminderSchedule_OrdersByFireTime_AcrossEvents()
    {
        var calendarId = NewCalendar().Id;
        var later = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 25, 9, 0), title: "Later");
        var earlier = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 7, 5, 9, 0), title: "Earlier");
        var reminders = EventProjection.GroupRemindersByEvent(new List<Reminder>
        {
            ReminderFor(later.Id, 10, ReminderOffsetUnit.Minutes),
            ReminderFor(earlier.Id, 10, ReminderOffsetUnit.Minutes),
        });

        var result = EventProjection.ReminderSchedule(
            new[] { later, earlier }, reminders, Utc(2026, 7, 1), Utc(2026, 8, 1));

        Assert.Equal(new[] { "Earlier", "Later" }, result.Select(r => r.Title));
    }

    [Fact]
    public void ReminderSchedule_CapturesMaxOffsetReminder_AtWindowEndBoundary()
    {
        // The domain caps reminder offsets at Reminder.MaxOffsetMinutes (4
        // weeks) specifically so MainWindow's fixed 31-day expansion pad
        // (ReminderHorizonPad) always comfortably exceeds the longest
        // offset Chronicle allows — see DECISIONS.md "Reminders: Post-Ship
        // Audit Positions" and REMINDERS.md "Horizon and padding". Worst
        // case: an event starting as late as possible while its
        // max-offset reminder still fires exactly at the window's upper
        // edge (inclusive).
        var calendarId = NewCalendar().Id;
        var windowStart = Utc(2026, 7, 1);
        var windowEnd = Utc(2026, 7, 31);
        var eventStart = windowEnd.AddMinutes(Reminder.MaxOffsetMinutes);

        var evt = StandaloneEvent(calendarId, startUtc: eventStart, title: "Renewal");
        var reminder = ReminderFor(evt.Id, 4, ReminderOffsetUnit.Weeks);
        var reminders = EventProjection.GroupRemindersByEvent(new List<Reminder> { reminder });

        var result = EventProjection.ReminderSchedule(
            new[] { evt }, reminders, windowStart, windowEnd);

        var only = Assert.Single(result);
        Assert.Equal(windowEnd, only.FireTimeUtc);
    }
}
