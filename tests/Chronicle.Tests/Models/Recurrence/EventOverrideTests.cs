using System;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

public class EventOverrideTests
{
    private static EventOverride Minimal() => new()
    {
        Id = Guid.NewGuid(),
        SeriesEventId = Guid.NewGuid(),
        OccurrenceAnchorUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
        UpdatedAtUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void Validate_MinimalOverride_DoesNotThrow()
    {
        Minimal().Validate();
    }

    [Fact]
    public void Validate_NonUtcAnchor_Throws()
    {
        var o = new EventOverride
        {
            Id = Guid.NewGuid(),
            SeriesEventId = Guid.NewGuid(),
            OccurrenceAnchorUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Local),
            UpdatedAtUtc = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc),
        };
        Assert.Throws<InvalidOperationException>(() => o.Validate());
    }

    [Fact]
    public void Validate_NonUtcStart_Throws()
    {
        var o = Minimal();
        o.StartTimeUtc = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Local);
        Assert.Throws<InvalidOperationException>(() => o.Validate());
    }

    [Fact]
    public void Validate_EndBeforeStart_Throws()
    {
        var o = Minimal();
        o.StartTimeUtc = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        o.EndTimeUtc = new DateTime(2026, 1, 2, 13, 0, 0, DateTimeKind.Utc);
        Assert.Throws<InvalidOperationException>(() => o.Validate());
    }

    [Fact]
    public void Validate_StartWithoutEnd_DoesNotThrow()
    {
        // End<Start is only checked when both override times are set.
        var o = Minimal();
        o.StartTimeUtc = new DateTime(2026, 1, 2, 14, 0, 0, DateTimeKind.Utc);
        o.Validate();
    }
}
