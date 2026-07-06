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
