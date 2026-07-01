using System;
using Chronicle.Models;

namespace Chronicle.Tests.Models;

public class EventValidationTests
{
    private static Event ValidEvent() => new()
    {
        Id = Guid.NewGuid(),
        CalendarId = Guid.NewGuid(),
        Title = "Test",
        StartTimeUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc),
        EndTimeUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc),
        CreatedAtUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Validate_ValidEvent_DoesNotThrow()
    {
        ValidEvent().Validate();
    }

    [Fact]
    public void Validate_NonUtcStart_Throws()
    {
        var evt = ValidEvent();
        evt.StartTimeUtc = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Local);
        Assert.Throws<InvalidOperationException>(() => evt.Validate());
    }

    [Fact]
    public void Validate_UnspecifiedKindStart_Throws()
    {
        var evt = ValidEvent();
        evt.StartTimeUtc = new DateTime(2026, 1, 1, 9, 0, 0); // DateTimeKind.Unspecified
        Assert.Throws<InvalidOperationException>(() => evt.Validate());
    }

    [Fact]
    public void Validate_EndBeforeStart_Throws()
    {
        var evt = ValidEvent();
        evt.EndTimeUtc = evt.StartTimeUtc.AddHours(-1);
        Assert.Throws<InvalidOperationException>(() => evt.Validate());
    }

    [Fact]
    public void Validate_EndEqualsStart_DoesNotThrow()
    {
        var evt = ValidEvent();
        evt.EndTimeUtc = evt.StartTimeUtc;
        evt.Validate();
    }

    [Fact]
    public void Validate_NonUtcExDate_Throws()
    {
        var evt = ValidEvent();
        evt.RecurrenceExDatesUtc = new[]
        {
            new DateTime(2026, 2, 1, 9, 0, 0, DateTimeKind.Local),
        };
        Assert.Throws<InvalidOperationException>(() => evt.Validate());
    }

    [Fact]
    public void Validate_InvalidTimeZoneId_Throws()
    {
        var evt = ValidEvent();
        evt.TimeZoneId = "Not/ARealZone";
        Assert.Throws<InvalidOperationException>(() => evt.Validate());
    }

    [Fact]
    public void Validate_ValidIanaTimeZoneId_DoesNotThrow()
    {
        // Resolves via TimeZoneInfo (ICU). If a known IANA zone fails to
        // resolve in this environment, that is a real environment problem
        // and this test should fail loudly rather than be silently skipped.
        var evt = ValidEvent();
        evt.TimeZoneId = "America/New_York";
        evt.Validate();
    }
}
