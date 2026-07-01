using System;
using Chronicle.Models;
using Chronicle.Models.Recurrence;

namespace Chronicle.Tests.Models.Recurrence;

public class EventKeyTests
{
    [Fact]
    public void For_StandaloneOrMaster_HasNullAnchorAndIsNotOccurrence()
    {
        var id = Guid.NewGuid();
        var evt = new Event { Id = id }; // SeriesAnchorUtc left null

        var key = EventKey.For(evt);

        Assert.Equal(id, key.SeriesId);
        Assert.Null(key.Anchor);
        Assert.False(key.IsOccurrence);
    }

    [Fact]
    public void For_Occurrence_PreservesIdAndAnchor()
    {
        var id = Guid.NewGuid();
        var anchor = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);
        var evt = new Event { Id = id, SeriesAnchorUtc = anchor };

        var key = EventKey.For(evt);

        Assert.Equal(id, key.SeriesId);
        Assert.Equal(anchor, key.Anchor);
        Assert.True(key.IsOccurrence);
    }

    [Fact]
    public void Equality_SameIdAndAnchor_AreEqual()
    {
        var id = Guid.NewGuid();
        var anchor = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(new EventKey(id, anchor), new EventKey(id, anchor));
    }

    [Fact]
    public void Equality_DifferentAnchor_AreNotEqual()
    {
        var id = Guid.NewGuid();
        var a = new EventKey(id, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var b = new EventKey(id, new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equality_MasterVsOccurrenceSameId_AreNotEqual()
    {
        var id = Guid.NewGuid();
        var master = new EventKey(id, null);
        var occurrence = new EventKey(id, new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(master, occurrence);
    }
}
