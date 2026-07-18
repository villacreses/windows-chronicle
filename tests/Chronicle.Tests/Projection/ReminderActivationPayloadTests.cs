using System;
using Chronicle.Models.Recurrence;
using Chronicle.Projection;

namespace Chronicle.Tests.Projection;

/// <summary>
/// The toast launch-argument codec is the shared identity format between the
/// scheduler (writes) and activation (reads). These pin the round-trip for
/// both <see cref="EventRef"/> variants — including exact UTC-anchor
/// fidelity — and the graceful-null behavior activation depends on.
/// </summary>
public class ReminderActivationPayloadTests
{
    [Fact]
    public void Master_RoundTrips()
    {
        var eventId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();

        var payload = ReminderActivationPayload.Encode(
            new EventRef.Master(eventId), reminderId);
        var decoded = ReminderActivationPayload.TryDecode(payload);

        Assert.NotNull(decoded);
        var master = Assert.IsType<EventRef.Master>(decoded!.Value.Ref);
        Assert.Equal(eventId, master.Id);
        Assert.Equal(reminderId, decoded.Value.ReminderId);
    }

    [Fact]
    public void Occurrence_RoundTrips_WithExactUtcAnchor()
    {
        var seriesId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        // Sub-second precision must survive — the anchor is identity, matched
        // bit-for-bit against the rule walk.
        var anchor = new DateTime(2026, 7, 14, 9, 0, 0, DateTimeKind.Utc)
            .AddTicks(1_234_567);

        var payload = ReminderActivationPayload.Encode(
            new EventRef.Occurrence(seriesId, anchor), reminderId);
        var decoded = ReminderActivationPayload.TryDecode(payload);

        Assert.NotNull(decoded);
        var occ = Assert.IsType<EventRef.Occurrence>(decoded!.Value.Ref);
        Assert.Equal(seriesId, occ.SeriesId);
        Assert.Equal(anchor, occ.AnchorUtc);
        Assert.Equal(DateTimeKind.Utc, occ.AnchorUtc.Kind);
        Assert.Equal(reminderId, decoded.Value.ReminderId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("m|not-a-guid|also-not")]
    [InlineData("o|only|three|x")]          // anchor not a long
    [InlineData("m|" + "00000000-0000-0000-0000-000000000000")] // too few parts
    [InlineData("x|a|b|c")]                 // unknown discriminator
    public void Malformed_DecodesToNull(string? payload)
    {
        Assert.Null(ReminderActivationPayload.TryDecode(payload));
    }
}
