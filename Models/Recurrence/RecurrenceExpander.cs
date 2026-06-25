using System;
using System.Collections.Generic;

namespace Chronicle.Models.Recurrence;

// Pure, allocation-light expansion of a recurring master `Event` into
// transient occurrence `Event`s. Occurrences are not persisted and exist
// only for the duration of one render-range load. Each occurrence
// carries `SeriesAnchorUtc` so the popover can scope edit/delete back to
// the originating master.
//
// COUNT semantics follow RFC 5545: COUNT counts every anchor the rule
// produces *before* EXDATE filtering. EXDATE'd anchors still consume a
// count. The range bounds are an output filter on top of both.
public static class RecurrenceExpander
{
    private const int SafetyCap = 10_000;

    public static IEnumerable<Event> Expand(
        Event master,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc,
        IReadOnlyList<EventOverride>? overrides = null)
    {
        if (master.RecurrenceRule is null)
            yield break;

        var rule = RecurrenceRule.Parse(master.RecurrenceRule);
        var duration = master.EndTimeUtc - master.StartTimeUtc;
        var exdates = master.RecurrenceExDatesUtc.Count == 0
            ? null
            : new HashSet<DateTime>(master.RecurrenceExDatesUtc);

        // Build the anchor lookup once per Expand call. Per-series
        // override count is small in practice (rarely >10); per-anchor
        // dictionary lookup keeps the inner loop O(1) regardless.
        Dictionary<DateTime, EventOverride>? overrideByAnchor = null;
        TimeSpan maxPastDisplacement = TimeSpan.Zero;
        if (overrides is { Count: > 0 })
        {
            overrideByAnchor = new Dictionary<DateTime, EventOverride>(overrides.Count);
            foreach (var ovr in overrides)
            {
                overrideByAnchor[ovr.OccurrenceAnchorUtc] = ovr;

                // If an override's merged Start is BEFORE its anchor,
                // it pulls a later anchor into an earlier render slot.
                // Without extending the walk-termination gate, we'd
                // stop iterating at rangeEnd and silently drop those
                // pulled-back occurrences. Track the maximum backward
                // displacement so the walk keeps going long enough.
                if (ovr.StartTimeUtc is DateTime s && s < ovr.OccurrenceAnchorUtc)
                {
                    var displacement = ovr.OccurrenceAnchorUtc - s;
                    if (displacement > maxPastDisplacement)
                        maxPastDisplacement = displacement;
                }
            }
        }

        // Walk-termination range: usually rangeEndUtc, but extended by
        // the largest backward displacement among the loaded overrides
        // so an override that pulls a future anchor into the visible
        // window is reached by the walker. COUNT and UNTIL termination
        // are unchanged — they're rule semantics, not range semantics.
        var walkEndUtc = rangeEndUtc + maxPastDisplacement;

        int generated = 0; // counts toward COUNT (pre-EXDATE, pre-override)
        int safety = 0;

        foreach (var anchor in WalkAnchors(master.StartTimeUtc, rule))
        {
            if (++safety > SafetyCap)
                yield break;

            if (rule.Count is int cap && generated >= cap)
                yield break;

            if (rule.UntilUtc is DateTime until && anchor > until)
                yield break;

            generated++;

            if (anchor > walkEndUtc)
                yield break;

            // EXDATE wins over override: a cancelled occurrence stays
            // cancelled even if an orphan override row points at the
            // same anchor. See DECISIONS.md "Named invariants" #3 / #6.
            if (exdates is not null && exdates.Contains(anchor))
                continue;

            EventOverride? overrideForAnchor = null;
            overrideByAnchor?.TryGetValue(anchor, out overrideForAnchor);

            // Merge override fields over projected fields. Null override
            // fields inherit from master (canonical iCalendar semantics).
            // End falls back to mergedStart + duration (not anchor +
            // duration) so duration "follows the start" when only one
            // side of the time range is overridden — the natural mental
            // model for "I moved this meeting; the meeting kept its
            // length." SeriesAnchorUtc on the emitted Event remains the
            // original anchor — the identity primitive into the
            // projection space, not the (possibly overridden) Start.
            var start = overrideForAnchor?.StartTimeUtc ?? anchor;
            var end = overrideForAnchor?.EndTimeUtc ?? (start + duration);

            // Range filter on merged times: an override can move an
            // occurrence out of the visible range from the rule's
            // perspective, or into it from past the walk-end. The
            // walk-termination extension above ensures we *reach* the
            // anchor; this filter decides whether the merged result
            // actually overlaps the visible window.
            if (end < rangeStartUtc)
                continue;
            if (start > rangeEndUtc)
                continue;

            yield return CloneAsOccurrence(
                master, anchor, start, end, overrideForAnchor);
        }
    }

    private static Event CloneAsOccurrence(
        Event master,
        DateTime anchorUtc,
        DateTime startUtc,
        DateTime endUtc,
        EventOverride? ovr)
    {
        return new Event
        {
            Id              = master.Id,
            CalendarId      = master.CalendarId,
            Title           = ovr?.Title ?? master.Title,
            Description     = ovr?.Description ?? master.Description,
            StartTimeUtc    = startUtc,
            EndTimeUtc      = endUtc,
            IsAllDay        = ovr?.IsAllDay ?? master.IsAllDay,

            // Intentionally cleared on the occurrence — only the master
            // row carries the rule. Renderers see a plain Event.
            RecurrenceRule         = null,
            RecurrenceExDatesUtc   = Array.Empty<DateTime>(),
            RecurrenceEndUtcCached = null,

            // Marks the instance as "occurrence of series `Id`". Stays
            // the ORIGINAL anchor even when the override moved Start —
            // EventKey identity is rule-walk-derived, not wall-clock.
            SeriesAnchorUtc = anchorUtc,

            CreatedAtUtc = master.CreatedAtUtc,
            UpdatedAtUtc = ovr?.UpdatedAtUtc ?? master.UpdatedAtUtc,
        };
    }

    private static IEnumerable<DateTime> WalkAnchors(
        DateTime startUtc,
        RecurrenceRule rule)
    {
        return rule.Frequency switch
        {
            RecurrenceFrequency.Daily   => WalkDaily(startUtc, rule.Interval),
            RecurrenceFrequency.Weekly  => WalkWeekly(startUtc, rule.Interval, rule.ByDay),
            RecurrenceFrequency.Monthly => WalkMonthly(startUtc, rule.Interval, rule.ByMonthDay),
            RecurrenceFrequency.Yearly  => WalkYearly(startUtc, rule.Interval),
            _ => throw new InvalidOperationException("Unknown frequency."),
        };
    }

    private static IEnumerable<DateTime> WalkDaily(DateTime start, int interval)
    {
        for (int i = 0; ; i++)
            yield return start.AddDays((long)i * interval);
    }

    private static IEnumerable<DateTime> WalkWeekly(
        DateTime start,
        int interval,
        WeekdaySet byDay)
    {
        // No BYDAY → recurs on the same weekday as `start`.
        if (byDay == WeekdaySet.None)
        {
            for (int i = 0; ; i++)
                yield return start.AddDays((long)i * 7 * interval);
            // unreachable
        }

        // BYDAY case: enumerate each matching weekday within the week,
        // then advance INTERVAL weeks. The first week emits anchors
        // ≥ `start` so the series begins at its declared start.
        var weekStart = StartOfWeek(start);
        var timeOfDay = start.TimeOfDay;

        for (int w = 0; ; w++)
        {
            var weekAnchor = weekStart.AddDays((long)w * 7 * interval);

            for (int d = 0; d < 7; d++)
            {
                var day = weekAnchor.AddDays(d);
                if ((byDay & RecurrenceRule.FromDayOfWeek(day.DayOfWeek)) == 0)
                    continue;

                var anchor = day.Date.Add(timeOfDay);
                anchor = DateTime.SpecifyKind(anchor, DateTimeKind.Utc);

                if (anchor < start)
                    continue;

                yield return anchor;
            }
        }
    }

    private static DateTime StartOfWeek(DateTime utc)
    {
        // Sunday-aligned to match Chronicle's grid convention.
        var dow = (int)utc.DayOfWeek;
        var date = utc.Date.AddDays(-dow);
        return DateTime.SpecifyKind(date, DateTimeKind.Utc);
    }

    private static IEnumerable<DateTime> WalkMonthly(
        DateTime start,
        int interval,
        int? byMonthDay)
    {
        var day = byMonthDay ?? start.Day;
        var timeOfDay = start.TimeOfDay;

        for (int i = 0; ; i++)
        {
            var month = start.AddMonths(i * interval);
            var daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);

            // RFC 5545: months that don't contain the target day are
            // skipped (e.g. Feb 30). We don't clamp to month-end.
            if (day > daysInMonth)
                continue;

            var anchor = new DateTime(
                month.Year, month.Month, day,
                timeOfDay.Hours, timeOfDay.Minutes, timeOfDay.Seconds,
                DateTimeKind.Utc);

            if (anchor < start)
                continue;

            yield return anchor;
        }
    }

    private static IEnumerable<DateTime> WalkYearly(DateTime start, int interval)
    {
        for (int i = 0; ; i++)
        {
            var year = start.Year + i * interval;
            // Feb 29 anchor in a non-leap year is skipped (RFC 5545
            // default — no BYDAY/BYMONTHDAY rewriting in MVP).
            if (start.Month == 2 && start.Day == 29
                && !DateTime.IsLeapYear(year))
                continue;

            yield return new DateTime(
                year, start.Month, start.Day,
                start.Hour, start.Minute, start.Second,
                DateTimeKind.Utc);
        }
    }

    // Computes the UTC end of the last occurrence for a finite series,
    // or null for infinite. Called by the writer when a rule is saved
    // so the range query can skip ended series cheaply. EXDATE does not
    // affect this — it only filters output, not series termination.
    public static DateTime? ComputeEndUtc(
        DateTime startUtc,
        TimeSpan duration,
        RecurrenceRule rule)
    {
        if (rule.UntilUtc is null && rule.Count is null)
            return null;

        DateTime lastAnchor = startUtc;
        int generated = 0;
        int safety = 0;

        foreach (var anchor in WalkAnchors(startUtc, rule))
        {
            if (++safety > SafetyCap)
                break;

            if (rule.UntilUtc is DateTime until && anchor > until)
                break;

            generated++;
            lastAnchor = anchor;

            if (rule.Count is int cap && generated >= cap)
                break;
        }

        return lastAnchor + duration;
    }
}
