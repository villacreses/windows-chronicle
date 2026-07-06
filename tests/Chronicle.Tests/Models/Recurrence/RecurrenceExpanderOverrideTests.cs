using System;
using System.Linq;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Models.Recurrence.RecurrenceTestSupport;

namespace Chronicle.Tests.Models.Recurrence;

public class RecurrenceExpanderOverrideTests
{
    [Fact]
    public void Override_ChangesFields_ButIdentityStaysOnRuleAnchor()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var ovr = Override(master.Id, Utc(2026, 1, 2, 9, 0),
            title: "Moved",
            startUtc: Utc(2026, 1, 2, 14, 0),
            endUtc: Utc(2026, 1, 2, 15, 0));

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59), new[] { ovr });

        var moved = occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 2, 9, 0));
        Assert.Equal("Moved", moved.Title);
        Assert.Equal(Utc(2026, 1, 2, 14, 0), moved.StartTimeUtc);
        Assert.Equal(Utc(2026, 1, 2, 15, 0), moved.EndTimeUtc);
        // Identity is the rule-walk anchor, not the overridden wall-clock start.
        Assert.NotEqual(moved.StartTimeUtc, moved.SeriesAnchorUtc);

        // Neighbours are untouched.
        Assert.Equal("Series", occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 1, 9, 0)).Title);
    }

    [Fact]
    public void Override_NullFields_InheritFromMaster()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0), duration: TimeSpan.FromHours(1));
        var ovr = Override(master.Id, Utc(2026, 1, 2, 9, 0), title: "Renamed only");

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59), new[] { ovr });

        var o = occ.Single(x => x.SeriesAnchorUtc == Utc(2026, 1, 2, 9, 0));
        Assert.Equal("Renamed only", o.Title);
        Assert.Equal(Utc(2026, 1, 2, 9, 0), o.StartTimeUtc);   // inherited anchor time
        Assert.Equal(Utc(2026, 1, 2, 10, 0), o.EndTimeUtc);    // inherited duration
    }

    [Fact]
    public void Override_StartOnly_EndFollowsDurationFromNewStart()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0), duration: TimeSpan.FromHours(2));
        var ovr = Override(master.Id, Utc(2026, 1, 2, 9, 0), startUtc: Utc(2026, 1, 2, 13, 0));

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59), new[] { ovr });

        var o = occ.Single(x => x.SeriesAnchorUtc == Utc(2026, 1, 2, 9, 0));
        Assert.Equal(Utc(2026, 1, 2, 13, 0), o.StartTimeUtc);
        Assert.Equal(Utc(2026, 1, 2, 15, 0), o.EndTimeUtc); // new start + 2h duration
    }

    [Fact]
    public void ExDate_WinsOverOverride_ForSameAnchor()
    {
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0),
            exDates: new[] { Utc(2026, 1, 2, 9, 0) });
        var ovr = Override(master.Id, Utc(2026, 1, 2, 9, 0), title: "Should not appear");

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59), new[] { ovr });

        Assert.Equal(
            new[] { Utc(2026,1,1,9,0), Utc(2026,1,3,9,0) },
            occ.Select(o => o.SeriesAnchorUtc!.Value));
        Assert.DoesNotContain(occ, o => o.Title == "Should not appear");
    }

    [Fact]
    public void Override_MovedBackwardAcrossRangeEnd_IsStillDiscovered()
    {
        // The Jan 10 anchor is past the visible range end (Jan 5), but the
        // override pulls its start back to Jan 4 (inside the range). The
        // walk-termination extension must reach the Jan 10 anchor.
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var ovr = Override(master.Id, Utc(2026, 1, 10, 9, 0),
            title: "Pulled back", startUtc: Utc(2026, 1, 4, 9, 0));

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 5, 23, 59), new[] { ovr });

        var pulled = occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 10, 9, 0));
        Assert.Equal("Pulled back", pulled.Title);
        Assert.Equal(Utc(2026, 1, 4, 9, 0), pulled.StartTimeUtc);
    }

    [Fact]
    public void Override_DescriptionAndIsAllDay_MergeIntoOccurrence()
    {
        // Phase A: the editor now writes Description and IsAllDay onto
        // OverrideFields when the user edits an occurrence. Confirm the
        // expander's merge honors both, and that a null field on the
        // override still inherits from the master (so an override
        // targeting only Description does not accidentally clear
        // IsAllDay or vice versa).
        var master = Master(
            "FREQ=DAILY",
            Utc(2026, 1, 1, 9, 0),
            duration: TimeSpan.FromHours(1));
        master.Description = "master notes";
        master.IsAllDay = false;

        var flippedBoth = Override(master.Id, Utc(2026, 1, 2, 9, 0),
            description: "override notes",
            isAllDay: true);
        var descriptionOnly = Override(master.Id, Utc(2026, 1, 3, 9, 0),
            description: "note only");

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 4, 23, 59),
            new[] { flippedBoth, descriptionOnly });

        var day2 = occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 2, 9, 0));
        Assert.Equal("override notes", day2.Description);
        Assert.True(day2.IsAllDay);

        var day3 = occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 3, 9, 0));
        Assert.Equal("note only", day3.Description);
        Assert.False(day3.IsAllDay); // null on override → inherited from master

        var day1 = occ.Single(o => o.SeriesAnchorUtc == Utc(2026, 1, 1, 9, 0));
        Assert.Equal("master notes", day1.Description);
        Assert.False(day1.IsAllDay);
    }

    [Fact]
    public void OrphanOverride_NotMatchingAnyAnchor_IsIgnored()
    {
        // An override anchored at 08:00 never matches a 09:00 walk anchor.
        var master = Master("FREQ=DAILY", Utc(2026, 1, 1, 9, 0));
        var ovr = Override(master.Id, Utc(2026, 1, 2, 8, 0), title: "Orphan");

        var occ = Expand(master, Utc(2026, 1, 1), Utc(2026, 1, 3, 23, 59), new[] { ovr });

        Assert.Equal(3, occ.Count);
        Assert.All(occ, o => Assert.Equal("Series", o.Title));
    }
}
