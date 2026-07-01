using System;
using Chronicle.Models;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

public class EventRefTests
{
    [Fact]
    public void From_MasterRow_ReturnsMaster()
    {
        var id = Guid.NewGuid();
        var evt = new Event { Id = id }; // no SeriesAnchorUtc

        var reference = EventRef.From(evt);

        var master = Assert.IsType<EventRef.Master>(reference);
        Assert.Equal(id, master.Id);
    }

    [Fact]
    public void From_Occurrence_ReturnsOccurrenceWithMasterIdAndAnchor()
    {
        // occurrence.Id == master.Id by the identity contract; the anchor is
        // the discriminator.
        var masterId = Guid.NewGuid();
        var anchor = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var evt = new Event { Id = masterId, SeriesAnchorUtc = anchor };

        var reference = EventRef.From(evt);

        var occ = Assert.IsType<EventRef.Occurrence>(reference);
        Assert.Equal(masterId, occ.SeriesId);
        Assert.Equal(anchor, occ.AnchorUtc);
    }
}
