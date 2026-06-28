using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Chronicle.Models.Recurrence;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

[Flags]
public enum WeekdaySet
{
    None      = 0,
    Sunday    = 1 << 0,
    Monday    = 1 << 1,
    Tuesday   = 1 << 2,
    Wednesday = 1 << 3,
    Thursday  = 1 << 4,
    Friday    = 1 << 5,
    Saturday  = 1 << 6,
}

// Parsed view of an RFC 5545 RRULE. MVP supports FREQ + INTERVAL + BYDAY
// (weekly) + BYMONTHDAY (monthly, single value) + COUNT *or* UNTIL.
// Anything outside that subset is rejected at parse time so we fail
// loudly rather than silently dropping rule semantics.
public sealed record RecurrenceRule(
    RecurrenceFrequency Frequency,
    int Interval,
    WeekdaySet ByDay,
    int? ByMonthDay,
    int? Count,
    DateTime? UntilUtc)
{
    public static RecurrenceRule Daily(int interval = 1)
        => new(RecurrenceFrequency.Daily, interval, WeekdaySet.None, null, null, null);

    public static RecurrenceRule Weekly(WeekdaySet days, int interval = 1)
        => new(RecurrenceFrequency.Weekly, interval, days, null, null, null);

    public static RecurrenceRule Monthly(int? byMonthDay = null, int interval = 1)
        => new(RecurrenceFrequency.Monthly, interval, WeekdaySet.None, byMonthDay, null, null);

    public static RecurrenceRule Yearly(int interval = 1)
        => new(RecurrenceFrequency.Yearly, interval, WeekdaySet.None, null, null, null);

    public RecurrenceRule WithCount(int count)
        => this with { Count = count, UntilUtc = null };

    public RecurrenceRule WithUntil(DateTime untilUtc)
    {
        if (untilUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("UNTIL must be UTC.", nameof(untilUtc));
        return this with { UntilUtc = untilUtc, Count = null };
    }

    public string ToRruleString()
    {
        var sb = new StringBuilder();
        sb.Append("FREQ=").Append(Frequency switch
        {
            RecurrenceFrequency.Daily   => "DAILY",
            RecurrenceFrequency.Weekly  => "WEEKLY",
            RecurrenceFrequency.Monthly => "MONTHLY",
            RecurrenceFrequency.Yearly  => "YEARLY",
            _ => throw new InvalidOperationException("Unknown frequency."),
        });

        if (Interval > 1)
        {
            sb.Append(";INTERVAL=").Append(
                Interval.ToString(CultureInfo.InvariantCulture));
        }

        if (ByDay != WeekdaySet.None)
        {
            sb.Append(";BYDAY=").Append(FormatByDay(ByDay));
        }

        if (ByMonthDay is int day)
        {
            sb.Append(";BYMONTHDAY=").Append(
                day.ToString(CultureInfo.InvariantCulture));
        }

        if (Count is int c)
        {
            sb.Append(";COUNT=").Append(
                c.ToString(CultureInfo.InvariantCulture));
        }
        else if (UntilUtc is DateTime u)
        {
            sb.Append(";UNTIL=").Append(u.ToString("yyyyMMddTHHmmssZ"));
        }

        return sb.ToString();
    }

    public static RecurrenceRule Parse(string rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            throw new FormatException("RRULE is empty.");

        RecurrenceFrequency? freq = null;
        int interval = 1;
        WeekdaySet byDay = WeekdaySet.None;
        int? byMonthDay = null;
        int? count = null;
        DateTime? until = null;

        foreach (var part in rrule.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                throw new FormatException($"Malformed RRULE part '{part}'.");

            var key = part.Substring(0, eq).Trim().ToUpperInvariant();
            var val = part.Substring(eq + 1).Trim();

            switch (key)
            {
                case "FREQ":
                    freq = val.ToUpperInvariant() switch
                    {
                        "DAILY"   => RecurrenceFrequency.Daily,
                        "WEEKLY"  => RecurrenceFrequency.Weekly,
                        "MONTHLY" => RecurrenceFrequency.Monthly,
                        "YEARLY"  => RecurrenceFrequency.Yearly,
                        _ => throw new FormatException(
                            $"Unsupported FREQ '{val}'."),
                    };
                    break;

                case "INTERVAL":
                    interval = int.Parse(val, CultureInfo.InvariantCulture);
                    if (interval < 1)
                        throw new FormatException("INTERVAL must be >= 1.");
                    break;

                case "BYDAY":
                    byDay = ParseByDay(val);
                    break;

                case "BYMONTHDAY":
                    byMonthDay = int.Parse(val, CultureInfo.InvariantCulture);
                    if (byMonthDay is < 1 or > 31)
                        throw new FormatException(
                            "BYMONTHDAY must be between 1 and 31.");
                    break;

                case "COUNT":
                    count = int.Parse(val, CultureInfo.InvariantCulture);
                    if (count < 1)
                        throw new FormatException("COUNT must be >= 1.");
                    break;

                case "UNTIL":
                    until = ParseUntil(val);
                    break;

                case "WKST":
                    // Sunday-aligned everywhere in Chronicle; ignore other
                    // values rather than failing — purely cosmetic.
                    break;

                default:
                    throw new FormatException(
                        $"Unsupported RRULE part '{key}'. MVP supports "
                        + "FREQ, INTERVAL, BYDAY, BYMONTHDAY, COUNT, UNTIL.");
            }
        }

        if (freq is null)
            throw new FormatException("RRULE is missing FREQ.");

        if (count is not null && until is not null)
            throw new FormatException(
                "RRULE cannot specify both COUNT and UNTIL.");

        if (byDay != WeekdaySet.None && freq != RecurrenceFrequency.Weekly)
            throw new FormatException(
                "BYDAY is only supported with FREQ=WEEKLY in MVP.");

        if (byMonthDay is not null && freq != RecurrenceFrequency.Monthly)
            throw new FormatException(
                "BYMONTHDAY is only supported with FREQ=MONTHLY in MVP.");

        return new RecurrenceRule(
            freq.Value, interval, byDay, byMonthDay, count, until);
    }

    private static DateTime ParseUntil(string value)
    {
        // RFC 5545: UNTIL of a UTC-anchored event MUST be in UTC form
        // (yyyyMMddTHHmmssZ). Date-only form (yyyyMMdd) is also allowed.
        if (value.Length == 8 &&
            DateTime.TryParseExact(
                value, "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateOnly))
        {
            return DateTime.SpecifyKind(dateOnly, DateTimeKind.Utc);
        }

        if (DateTime.TryParseExact(
                value, "yyyyMMddTHHmmssZ",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        throw new FormatException(
            $"UNTIL '{value}' is not a valid RFC 5545 UTC datetime.");
    }

    private static WeekdaySet ParseByDay(string value)
    {
        var set = WeekdaySet.None;
        foreach (var tok in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            set |= tok.Trim().ToUpperInvariant() switch
            {
                "SU" => WeekdaySet.Sunday,
                "MO" => WeekdaySet.Monday,
                "TU" => WeekdaySet.Tuesday,
                "WE" => WeekdaySet.Wednesday,
                "TH" => WeekdaySet.Thursday,
                "FR" => WeekdaySet.Friday,
                "SA" => WeekdaySet.Saturday,
                _ => throw new FormatException(
                    $"Unsupported BYDAY token '{tok}'. Ordinal prefixes "
                    + "(e.g. '2MO') are deferred to Phase 2."),
            };
        }

        if (set == WeekdaySet.None)
            throw new FormatException("BYDAY must specify at least one day.");

        return set;
    }

    private static string FormatByDay(WeekdaySet days)
    {
        var parts = new List<string>(7);
        if ((days & WeekdaySet.Sunday)    != 0) parts.Add("SU");
        if ((days & WeekdaySet.Monday)    != 0) parts.Add("MO");
        if ((days & WeekdaySet.Tuesday)   != 0) parts.Add("TU");
        if ((days & WeekdaySet.Wednesday) != 0) parts.Add("WE");
        if ((days & WeekdaySet.Thursday)  != 0) parts.Add("TH");
        if ((days & WeekdaySet.Friday)    != 0) parts.Add("FR");
        if ((days & WeekdaySet.Saturday)  != 0) parts.Add("SA");
        return string.Join(",", parts);
    }

    internal static WeekdaySet FromDayOfWeek(DayOfWeek dow) => dow switch
    {
        DayOfWeek.Sunday    => WeekdaySet.Sunday,
        DayOfWeek.Monday    => WeekdaySet.Monday,
        DayOfWeek.Tuesday   => WeekdaySet.Tuesday,
        DayOfWeek.Wednesday => WeekdaySet.Wednesday,
        DayOfWeek.Thursday  => WeekdaySet.Thursday,
        DayOfWeek.Friday    => WeekdaySet.Friday,
        DayOfWeek.Saturday  => WeekdaySet.Saturday,
        _ => WeekdaySet.None,
    };
}
