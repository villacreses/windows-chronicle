using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Projection;

/// <summary>
/// The pure event-pipeline logic that turns repository rows into the
/// day-grouped projection the UI renders — extracted from <c>MainWindow</c>
/// so it stays provider-neutral and testable (see <c>.context/TESTING.md</c>
/// "Projection Helper Extraction"). No WinUI, no database: inputs in,
/// projection out.
///
/// The four operations are the seams a new event source (a Phase B view, a
/// provider adapter) must reuse rather than re-implement:
///   1. group per-occurrence overrides by series,
///   2. expand recurring masters into occurrences (masters never survive),
///   3. apply calendar visibility and group by local day,
///   4. decide whether a loaded UTC range already covers a requested one.
/// </summary>
internal static class EventProjection
{
    /// <summary>
    /// Buckets a flat override list by <c>SeriesEventId</c> so the expander
    /// can do an O(1) lookup per series. Allocates one list per series with
    /// overrides; series without overrides are absent from the dictionary
    /// (the expander treats absence as "no overrides").
    /// </summary>
    public static Dictionary<Guid, IReadOnlyList<EventOverride>> GroupOverridesBySeries(
        IReadOnlyList<EventOverride> overrides)
    {
        if (overrides.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<EventOverride>>();

        var result = new Dictionary<Guid, List<EventOverride>>();
        foreach (var ovr in overrides)
        {
            if (!result.TryGetValue(ovr.SeriesEventId, out var bucket))
            {
                bucket = new List<EventOverride>();
                result[ovr.SeriesEventId] = bucket;
            }
            bucket.Add(ovr);
        }

        var typed = new Dictionary<Guid, IReadOnlyList<EventOverride>>(result.Count);
        foreach (var (k, v) in result)
            typed[k] = v;
        return typed;
    }

    /// <summary>
    /// Flattens repository rows into the projection that
    /// <see cref="GroupVisibleByDay"/> ultimately groups: standalone rows pass
    /// through; recurring master rows are replaced by their expansions over the
    /// load range, with any per-occurrence overrides for the series merged in
    /// by the expander. Masters never enter the result — only their expansions
    /// do. Expansion is bounded by the active view's range (≤ 42 days), so the
    /// per-call cost is small even for long-lived series.
    /// </summary>
    public static List<Event> ExpandRecurrences(
        IReadOnlyList<Event> rows,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyDictionary<Guid, IReadOnlyList<EventOverride>> overridesBySeries)
    {
        var result = new List<Event>(rows.Count);

        foreach (var row in rows)
        {
            if (row.RecurrenceRule is null)
            {
                result.Add(row);
                continue;
            }

            var seriesOverrides = overridesBySeries.GetValueOrDefault(row.Id);
            foreach (var occurrence in
                RecurrenceExpander.Expand(row, rangeStartUtc, rangeEndUtc, seriesOverrides))
            {
                result.Add(occurrence);
            }
        }

        return result;
    }

    /// <summary>
    /// Filters the projected events through calendar visibility, then groups
    /// the survivors by local day key for render lookup, each day's list
    /// ordered by <see cref="OrderForDay"/>. Pure — no DB. An empty visibility
    /// map treats every calendar as visible (the no-calendars and
    /// not-yet-reconciled cases). Does not mutate the source list.
    /// </summary>
    public static Dictionary<DateTime, List<Event>> GroupVisibleByDay(
        IReadOnlyList<Event> projectedEvents,
        IReadOnlyDictionary<Guid, bool> calendarVisibility)
    {
        var visible = projectedEvents
            .Where(e => calendarVisibility.Count == 0
                        || calendarVisibility.GetValueOrDefault(e.CalendarId, true));

        return visible
            .GroupBy(e => DateHelpers.GetEventDayKey(e.StartTimeUtc))
            .ToDictionary(g => g.Key, g => OrderForDay(g));
    }

    /// <summary>
    /// Orders a single day's events for display: all-day events first (the
    /// convention the day/week all-day bands and the selected-day panel share),
    /// then timed events by start instant. Ties within each group break by
    /// title (case-insensitive) so the order is deterministic and stable across
    /// reloads. Because <c>_eventsByDate</c> is a render cache, not an identity
    /// source, a stable render order must be computed here rather than relied
    /// upon from repository query order (which sorts by each series' master
    /// start, not by occurrence time).
    /// </summary>
    public static List<Event> OrderForDay(IEnumerable<Event> events)
        => events
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.IsAllDay ? DateTime.MinValue : e.StartTimeUtc)
            .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Runs the search side of the projection pipeline: takes the candidate
    /// rows returned by <c>EventRepository.SearchCandidatesAsync</c>,
    /// expands recurring masters over the load range, and re-checks each
    /// merged occurrence's Title / Description against the query — because
    /// an override can flip either field either direction, and the SQL
    /// candidate step matches on stored strings, not merged ones.
    ///
    /// Standalone rows are kept iff their Title or Description contains
    /// the query. Recurring masters are expanded via
    /// <see cref="RecurrenceExpander.Expand"/> using
    /// <paramref name="overridesBySeries"/>; each occurrence is retained
    /// iff its merged Title or Description matches. The result is a flat
    /// list of standalone events plus expanded occurrences, ordered by
    /// <c>StartTimeUtc</c> for a chronological results panel.
    ///
    /// <para>Match is ordinal case-insensitive — close to the SQL layer's
    /// ASCII <c>LIKE</c> behavior, and slightly more forgiving for
    /// Unicode case (safely additive, never subtractive of SQL hits that
    /// already passed).</para>
    ///
    /// <para>Deliberately does not take a calendar visibility map. The
    /// load pipeline honors visibility because the grid is a
    /// chronological picture; search is intent-driven — the query is the
    /// filter. Hidden calendars are still searchable.</para>
    ///
    /// <para>Empty / whitespace <paramref name="query"/> returns an empty
    /// list, matching the repository's behavior.</para>
    /// </summary>
    public static List<Event> SearchOccurrences(
        IReadOnlyList<Event> candidateRows,
        IReadOnlyDictionary<Guid, IReadOnlyList<EventOverride>> overridesBySeries,
        string query,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<Event>();

        var results = new List<Event>();

        foreach (var row in candidateRows)
        {
            if (row.RecurrenceRule is null)
            {
                if (Matches(row, query))
                    results.Add(row);
                continue;
            }

            var seriesOverrides = overridesBySeries.GetValueOrDefault(row.Id);
            foreach (var occurrence in
                RecurrenceExpander.Expand(row, rangeStartUtc, rangeEndUtc, seriesOverrides))
            {
                if (Matches(occurrence, query))
                    results.Add(occurrence);
            }
        }

        results.Sort(CompareByStartThenTitle);
        return results;
    }

    private static bool Matches(Event e, string query)
    {
        if (e.Title is string title
            && title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        if (e.Description is string description
            && description.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static int CompareByStartThenTitle(Event a, Event b)
    {
        var byStart = a.StartTimeUtc.CompareTo(b.StartTimeUtc);
        if (byStart != 0) return byStart;
        return string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the cached (loaded) UTC range already covers the requested UTC
    /// range, so the load pipeline can skip the DB query. View switches that
    /// stay inside the loaded range (Month → Week → Day in place) short-circuit
    /// on this.
    /// </summary>
    public static bool RangeCovered(
        DateTime loadedStartUtc,
        DateTime loadedEndUtc,
        DateTime requestedStartUtc,
        DateTime requestedEndUtc)
        => requestedStartUtc >= loadedStartUtc && requestedEndUtc <= loadedEndUtc;
}
