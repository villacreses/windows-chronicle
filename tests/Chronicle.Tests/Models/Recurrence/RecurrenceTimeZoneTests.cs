using System;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

/// <summary>
/// Layer 5 — the write-boundary timezone normalization extracted from
/// <c>EventEditPopover</c>. Enforces the "persisted TimeZoneId is always IANA"
/// invariant: Windows ids convert to IANA, IANA ids pass through, and an
/// unmappable id degrades to UTC rather than persisting an unmapped string.
/// (.NET carries the Windows↔IANA mapping cross-platform via ICU.)
/// </summary>
public sealed class RecurrenceTimeZoneTests
{
    [Fact]
    public void NormalizeToIana_WindowsId_ConvertsToIana()
    {
        var result = RecurrenceTimeZone.NormalizeToIana("Pacific Standard Time");

        Assert.NotEqual("Pacific Standard Time", result);
        Assert.Contains("/", result); // IANA form, e.g. "America/Los_Angeles"
        // And it is a real IANA id (round-trips back to a Windows id).
        Assert.True(TimeZoneInfo.TryConvertIanaIdToWindowsId(result, out _));
    }

    [Fact]
    public void NormalizeToIana_IanaId_ReturnsSameId()
    {
        Assert.Equal(
            "America/New_York",
            RecurrenceTimeZone.NormalizeToIana("America/New_York"));
    }

    [Fact]
    public void NormalizeToIana_UnrecognizedId_FallsBackToUtc()
    {
        Assert.Equal("UTC", RecurrenceTimeZone.NormalizeToIana("Definitely Not A Zone"));
    }
}
