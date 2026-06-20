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
        DateTime rangeEndUtc)
    {
        if (master.RecurrenceRule is null)
            yield break;

        var rule = RecurrenceRule.Parse(master.RecurrenceRule);
        var duration = master.EndTimeUtc - master.StartTimeUtc;
        var exdates = master.RecurrenceExDatesUtc.Count == 0
            ? null
            : new HashSet<DateTime>(master.RecurrenceExDatesUtc);

        int generated = 0; // counts toward COUNT (pre-EXDATE)
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

            // Output-range filter — we don't break on `> rangeEndUtc`
            // because BYDAY can emit several anchors per week, and the
            // walk is monotonic per week but not per anchor across the
            // week's BYDAY set. The WalkAnchors implementations bound
            // their own iteration; this filter just skips out-of-range
            // outputs.
            if (anchor > rangeEndUtc)
                yield break;

            if (exdates is not null && exdates.Contains(anchor))
                continue;

            var end = anchor + duration;
            if (end < rangeStartUtc)
                continue;

            yield return CloneAsOccurrence(master, anchor, end);
        }
    }

    private static Event CloneAsOccurrence(
        Event master,
        DateTime anchorUtc,
        DateTime endUtc)
    {
        return new Event
        {
            Id              = master.Id,
            CalendarId      = master.CalendarId,
            Title           = master.Title,
            Description     = master.Description,
            StartTimeUtc    = anchorUtc,
            EndTimeUtc      = endUtc,
            IsAllDay        = master.IsAllDay,

            // Intentionally cleared on the occurrence — only the master
            // row carries the rule. Renderers see a plain Event.
            RecurrenceRule         = null,
            RecurrenceExDatesUtc   = Array.Empty<DateTime>(),
            RecurrenceEndUtcCached = null,

            // Marks the instance as "occurrence of series `Id`".
            SeriesAnchorUtc = anchorUtc,

            CreatedAtUtc = master.CreatedAtUtc,
            UpdatedAtUtc = master.UpdatedAtUtc,
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
