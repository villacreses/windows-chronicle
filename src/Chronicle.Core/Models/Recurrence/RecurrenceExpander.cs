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

    // Padding added to walk-termination gates when a tz-aware walk is
    // active. Shift-forward handling at DST gaps causes a one-time
    // non-monotonic blip in the projected UTC anchor sequence (the gap-
    // day anchor is shifted up by ~1 hour, then the next day's anchor
    // returns to the normal UTC). Without padding, a termination gate
    // landing in that blip would prematurely cut off subsequent valid
    // anchors.
    //
    // HEURISTIC, not a protocol constant. One hour covers every modern
    // IANA zone (Lord Howe's 30-minute delta is well within); we don't
    // compute per-zone because over-padding has no correctness cost
    // (just one extra anchor checked at the boundary) and the
    // GetAdjustmentRules() lookup would add per-Expand overhead. If a
    // real zone with a >1-hour DST shift ever needs to be supported,
    // switch this to a per-zone computation — nothing in the algorithm
    // depends on the value being exactly one hour. Zero when no tz is
    // set so legacy UTC-anchored walks see no behavior change.
    private static readonly TimeSpan TzWalkPad = TimeSpan.FromHours(1);

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
        // window is reached by the walker, and (when tz-aware) by
        // TzWalkPad to absorb the shift-forward UTC blip at DST gaps.
        // COUNT and UNTIL termination are unchanged — they're rule
        // semantics, not range semantics (UNTIL gets its own pad inside
        // the loop below).
        var dstPad = master.TimeZoneId is null ? TimeSpan.Zero : TzWalkPad;
        var walkEndUtc = rangeEndUtc + maxPastDisplacement + dstPad;

        int generated = 0; // counts toward COUNT (pre-EXDATE, pre-override)
        int safety = 0;

        foreach (var anchor in WalkAnchorsForMaster(master.StartTimeUtc, rule, master.TimeZoneId))
        {
            if (++safety > SafetyCap)
                yield break;

            if (rule.Count is int cap && generated >= cap)
                yield break;

            // UNTIL termination: pad by dstPad before breaking so the
            // shift-forward blip in a tz-aware walk doesn't cut off a
            // subsequent valid anchor. Anchors temporarily past
            // unpadded UNTIL still pass through the walker but are
            // skipped by the per-iteration `continue` below.
            if (rule.UntilUtc is DateTime until && anchor > until + dstPad)
                yield break;

            generated++;

            if (rule.UntilUtc is DateTime untilHard && anchor > untilHard)
                continue;

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

    // Single dispatch shared by Expand and ComputeEndUtc — guarantees the
    // two callers see the same anchor sequence under any walk strategy.
    // The tz-aware branch walks in local time and projects each anchor to
    // UTC at emission, with shift-forward handling for invalid local
    // times. Bad TimeZoneId values degrade to the legacy UTC walk with a
    // debug log (defense in depth — see DECISIONS.md "Named invariants").
    private static IEnumerable<DateTime> WalkAnchorsForMaster(
        DateTime startUtc,
        RecurrenceRule rule,
        string? timeZoneId)
    {
        if (timeZoneId is null)
        {
            foreach (var anchor in WalkAnchors(startUtc, rule))
                yield return anchor;
            yield break;
        }

        // C# disallows yield inside a catch clause, so the bad-tz
        // fallback is split: catch sets `tz` to null and we yield the
        // legacy UTC walk after the try.
        TimeZoneInfo? tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException
                                || ex is InvalidTimeZoneException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"RecurrenceExpander: TimeZoneId '{timeZoneId}' did not "
                + $"resolve ({ex.GetType().Name}); falling back to legacy "
                + "UTC walk for this series.");
            tz = null;
        }

        if (tz is null)
        {
            foreach (var anchor in WalkAnchors(startUtc, rule))
                yield return anchor;
            yield break;
        }

        // Convert the master's UTC start into the anchor zone's local
        // time, then walk in local semantics. The walker primitives
        // return Kind=Utc by implementation, but we treat their output
        // as local-clock readings here — SpecifyKind(Unspecified) so
        // ConvertTimeToUtc accepts them.
        var startLocal = DateTime.SpecifyKind(
            TimeZoneInfo.ConvertTimeFromUtc(startUtc, tz),
            DateTimeKind.Unspecified);

        foreach (var rawLocal in WalkAnchors(startLocal, rule))
        {
            var local = DateTime.SpecifyKind(rawLocal, DateTimeKind.Unspecified);
            local = ResolveLocalForDst(local, tz);
            yield return TimeZoneInfo.ConvertTimeToUtc(local, tz);
        }
    }

    // Spring-forward gap (e.g. 2:30 AM doesn't exist on March 10, 2024
    // in NY): shift forward by the DST adjustment delta so the event
    // still occurs on its scheduled day, matching Google / Apple /
    // libical convention. Ambiguous fall-back times are left alone —
    // TimeZoneInfo.ConvertTimeToUtc defaults to standard time, which
    // also matches convention. The defensive loop guards against
    // pathological zones with sub-hour deltas (Lord Howe) or historical
    // irregularities; bails after 4 iterations to avoid infinite loops.
    private static DateTime ResolveLocalForDst(DateTime local, TimeZoneInfo tz)
    {
        for (int i = 0; i < 4 && tz.IsInvalidTime(local); i++)
        {
            var delta = FindAdjustmentRule(tz, local)?.DaylightDelta
                ?? TimeSpan.FromHours(1);
            local = local.Add(delta);
        }
        return local;
    }

    private static TimeZoneInfo.AdjustmentRule? FindAdjustmentRule(
        TimeZoneInfo tz, DateTime local)
    {
        foreach (var rule in tz.GetAdjustmentRules())
        {
            if (rule.DateStart <= local && local <= rule.DateEnd)
                return rule;
        }
        return null;
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
    //
    // Routes through WalkAnchorsForMaster so the cached end is computed
    // under the same walk strategy the renderer will use — preventing
    // drift between the two code paths under tz-aware anchoring.
    public static DateTime? ComputeEndUtc(
        DateTime startUtc,
        TimeSpan duration,
        RecurrenceRule rule,
        string? timeZoneId)
    {
        if (rule.UntilUtc is null && rule.Count is null)
            return null;

        var dstPad = timeZoneId is null ? TimeSpan.Zero : TzWalkPad;

        DateTime lastAnchor = startUtc;
        int generated = 0;
        int safety = 0;

        foreach (var anchor in WalkAnchorsForMaster(startUtc, rule, timeZoneId))
        {
            if (++safety > SafetyCap)
                break;

            // UNTIL: pad by dstPad before breaking (shift-forward blip)
            // and continue past anchors temporarily exceeding the
            // unpadded UNTIL without counting them or updating
            // lastAnchor — matches the Expand termination semantics.
            if (rule.UntilUtc is DateTime until && anchor > until + dstPad)
                break;
            if (rule.UntilUtc is DateTime untilHard && anchor > untilHard)
                continue;

            generated++;
            lastAnchor = anchor;

            if (rule.Count is int cap && generated >= cap)
                break;
        }

        return lastAnchor + duration;
    }
}
