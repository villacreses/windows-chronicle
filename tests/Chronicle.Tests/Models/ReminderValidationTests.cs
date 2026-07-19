using System;
using Chronicle.Models;

namespace Chronicle.Tests.Models;

/// <summary>
/// <see cref="Reminder"/> is a pure domain child of the Event aggregate:
/// a stored-as-expressed offset (quantity + unit) with a derived minute
/// value. These tests pin the validation boundary and the fixed-duration
/// arithmetic (see REMINDERS.md "Offsets are fixed durations").
/// </summary>
public class ReminderValidationTests
{
    private static Reminder Valid(int quantity = 10,
        ReminderOffsetUnit unit = ReminderOffsetUnit.Minutes) => new()
    {
        Id = Guid.NewGuid(),
        EventId = Guid.NewGuid(),
        OffsetQuantity = quantity,
        OffsetUnit = unit,
    };

    [Fact]
    public void Validate_NegativeQuantity_Throws()
    {
        var reminder = Valid(quantity: -1);
        Assert.Throws<InvalidOperationException>(() => reminder.Validate());
    }

    [Fact]
    public void Validate_ZeroQuantity_DoesNotThrow()
    {
        // (0, anything) = "at start time" — a valid reminder.
        Valid(quantity: 0).Validate();
    }

    [Theory]
    [InlineData(1, ReminderOffsetUnit.Minutes, 1)]
    [InlineData(45, ReminderOffsetUnit.Minutes, 45)]
    [InlineData(1, ReminderOffsetUnit.Hours, 60)]
    [InlineData(3, ReminderOffsetUnit.Hours, 180)]
    [InlineData(1, ReminderOffsetUnit.Days, 1440)]
    [InlineData(2, ReminderOffsetUnit.Weeks, 20160)]
    [InlineData(0, ReminderOffsetUnit.Weeks, 0)]
    public void OffsetMinutes_DerivesFixedDurations(
        int quantity, ReminderOffsetUnit unit, int expectedMinutes)
    {
        Assert.Equal(expectedMinutes, Valid(quantity, unit).OffsetMinutes);
    }

    // ── Bounded offset (Local Baseline Addendum) ──────────────────────────
    //
    // Maximum 4 weeks, enforced at the single write chokepoint
    // (ReminderRepository.SetForEventAsync calls Validate per reminder).
    // See DECISIONS.md "Reminders: Post-Ship Audit Positions" — the bound
    // is chosen specifically to stay under MainWindow's fixed 31-day
    // ReminderHorizonPad (see REMINDERS.md "Horizon and padding").

    [Fact]
    public void Validate_ExactlyFourWeeks_DoesNotThrow()
    {
        Valid(quantity: 4, unit: ReminderOffsetUnit.Weeks).Validate();
    }

    [Fact]
    public void Validate_OverFourWeeksInWeeks_Throws()
    {
        var reminder = Valid(quantity: 5, unit: ReminderOffsetUnit.Weeks);
        Assert.Throws<InvalidOperationException>(() => reminder.Validate());
    }

    [Fact]
    public void Validate_OverFourWeeksInMinutes_Throws()
    {
        // Same bound regardless of which unit expresses it — the invariant
        // is on total duration (OffsetMinutes), not on the unit chosen.
        var reminder = Valid(
            quantity: Reminder.MaxOffsetMinutes + 1, unit: ReminderOffsetUnit.Minutes);
        Assert.Throws<InvalidOperationException>(() => reminder.Validate());
    }

    [Fact]
    public void MaxOffsetMinutes_IsExactlyFourWeeks()
    {
        Assert.Equal(4 * 7 * 24 * 60, Reminder.MaxOffsetMinutes);
    }
}
