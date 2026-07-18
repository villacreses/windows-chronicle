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
}
