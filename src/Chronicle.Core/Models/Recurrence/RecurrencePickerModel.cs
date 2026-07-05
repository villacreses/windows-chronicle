using Chronicle.Helpers;
using System;
using System.Globalization;

namespace Chronicle.Models.Recurrence;

/// <summary>Frequency options the editor's Repeats picker offers.</summary>
internal enum RecurrenceFrequencyChoice { None, Daily, Weekly, Monthly, Yearly }

/// <summary>How a recurring series terminates in the picker.</summary>
internal enum RecurrenceEndChoice { Never, OnDate, AfterN }

/// <summary>
/// The picker's logical state, independent of WinUI controls. The combo
/// selections map to the enum ordinals; <see cref="WeeklyDays"/> mirrors the
/// day chips; <see cref="UntilLocalDate"/> is the (local, date-only) value of
/// the "On date" picker; <see cref="CountText"/> is the raw "After N" text.
/// </summary>
internal readonly record struct RecurrencePickerState(
    RecurrenceFrequencyChoice Frequency,
    WeekdaySet WeeklyDays,
    RecurrenceEndChoice End,
    DateTime UntilLocalDate,
    string CountText);

/// <summary>
/// The pure recurrence-picker logic extracted from <c>EventEditPopover</c>
/// (see <c>.context/TESTING.md</c> Layer 5 "recurrence picker rule
/// construction"): build a <see cref="RecurrenceRule"/> from picker state
/// (with the same validation the form surfaces inline), and seed picker state
/// back from an existing rule. The popover keeps the WinUI control wiring and
/// calls into this; the mapping and validation are now testable without a UI.
///
/// The picker represents a deliberate subset of the RRULE surface — INTERVAL
/// is always 1, monthly is always BYMONTHDAY on the start day — so a rule
/// round-trips only within that subset.
/// </summary>
internal static class RecurrencePickerModel
{
    private const string DefaultCountText = "10";

    /// <summary>
    /// Builds the rule for <paramref name="state"/> against the event's
    /// <paramref name="startUtc"/> (monthly uses the start's local day-of-month;
    /// the "On date" end is validated against the start). Returns
    /// <c>(null, null, true)</c> for "Does not repeat", <c>(rule, null, true)</c>
    /// on success, or <c>(null, error, false)</c> with a user-facing message.
    /// </summary>
    public static (RecurrenceRule? rule, string? error, bool ok) BuildRule(
        RecurrencePickerState state,
        DateTime startUtc)
    {
        if (state.Frequency == RecurrenceFrequencyChoice.None)
            return (null, null, true);

        RecurrenceRule rule;
        switch (state.Frequency)
        {
            case RecurrenceFrequencyChoice.Daily:
                rule = RecurrenceRule.Daily();
                break;

            case RecurrenceFrequencyChoice.Weekly:
                if (state.WeeklyDays == WeekdaySet.None)
                    return (null, "Pick at least one day of the week.", false);
                rule = RecurrenceRule.Weekly(state.WeeklyDays);
                break;

            case RecurrenceFrequencyChoice.Monthly:
                rule = RecurrenceRule.Monthly(byMonthDay: startUtc.ToLocalTime().Day);
                break;

            case RecurrenceFrequencyChoice.Yearly:
                rule = RecurrenceRule.Yearly();
                break;

            default:
                return (null, "Unsupported repeat option.", false);
        }

        switch (state.End)
        {
            case RecurrenceEndChoice.Never:
                break;

            case RecurrenceEndChoice.OnDate:
                // The picker's date is inclusive: extend to the last tick of
                // that local day before comparing / storing.
                var until = DateHelpers
                    .CombineLocalDateAndTimeAsUtc(state.UntilLocalDate.Date, TimeSpan.Zero)
                    .AddDays(1).AddTicks(-1);
                if (until < startUtc)
                    return (null, "End date must be on or after the start date.", false);
                rule = rule.WithUntil(until);
                break;

            case RecurrenceEndChoice.AfterN:
                if (!int.TryParse(
                        state.CountText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var count)
                    || count < 1)
                {
                    return (null, "Occurrence count must be a positive integer.", false);
                }
                rule = rule.WithCount(count);
                break;
        }

        return (rule, null, true);
    }

    /// <summary>
    /// Derives picker state from an existing rule (or "Does not repeat" when
    /// <paramref name="rule"/> is null), for pre-filling the form.
    /// <paramref name="startLocal"/> supplies the default weekly day (the
    /// start's weekday) and the default "On date" value.
    /// </summary>
    public static RecurrencePickerState SeedState(RecurrenceRule? rule, DateTime startLocal)
    {
        var defaultUntil = startLocal.AddMonths(3).Date;

        if (rule is null)
        {
            return new RecurrencePickerState(
                RecurrenceFrequencyChoice.None,
                SingleDay(startLocal.DayOfWeek),
                RecurrenceEndChoice.Never,
                defaultUntil,
                DefaultCountText);
        }

        var frequency = rule.Frequency switch
        {
            RecurrenceFrequency.Daily   => RecurrenceFrequencyChoice.Daily,
            RecurrenceFrequency.Weekly  => RecurrenceFrequencyChoice.Weekly,
            RecurrenceFrequency.Monthly => RecurrenceFrequencyChoice.Monthly,
            RecurrenceFrequency.Yearly  => RecurrenceFrequencyChoice.Yearly,
            _ => RecurrenceFrequencyChoice.None,
        };

        // A rule with BYDAY drives the chips; otherwise default to the start's
        // weekday so flipping to Weekly yields a sensible rule immediately.
        var weeklyDays = rule.ByDay != WeekdaySet.None
            ? rule.ByDay
            : SingleDay(startLocal.DayOfWeek);

        var end = RecurrenceEndChoice.Never;
        var until = defaultUntil;
        var countText = DefaultCountText;

        if (rule.UntilUtc is DateTime untilUtc)
        {
            end = RecurrenceEndChoice.OnDate;
            until = untilUtc.ToLocalTime().Date;
        }
        else if (rule.Count is int count)
        {
            end = RecurrenceEndChoice.AfterN;
            countText = count.ToString(CultureInfo.InvariantCulture);
        }

        return new RecurrencePickerState(frequency, weeklyDays, end, until, countText);
    }

    private static WeekdaySet SingleDay(DayOfWeek dayOfWeek)
        => RecurrenceRule.FromDayOfWeek(dayOfWeek);
}
