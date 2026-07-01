using System;
using System.Linq;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Models.Recurrence.RecurrenceTestSupport;

namespace Chronicle.Tests.Models.Recurrence;

// DST tests pin an explicit zone (America/New_York) and explicit 2026
// transition dates (spring-forward Mar 8, fall-back Nov 1). The zone is
// resolved once in a static initializer; if it fails to resolve in this
// environment the whole class fails loudly rather than silently skipping.
public class RecurrenceExpanderDstTests
{
    private const string NewYork = "America/New_York";
    private static readonly TimeZoneInfo Ny =
        TimeZoneInfo.FindSystemTimeZoneById(NewYork);

    [Fact]
    public void TzAnchored_Weekly_PreservesWallClockAcrossSpringForward()
    {
        // 09:00 New York, weekly. Before DST (Mar 6) that is 14:00 UTC;
        // after DST (Mar 13) it is 13:00 UTC — the same 9 AM wall clock.
        var master = Master("FREQ=WEEKLY", Utc(2026, 2, 27, 14, 0), timeZoneId: NewYork);
        var occ = Expand(master, Utc(2026, 2, 20), Utc(2026, 3, 20, 23, 59));

        Assert.Contains(Utc(2026, 3, 6, 14, 0), occ.Select(o => o.StartTimeUtc));
        Assert.Contains(Utc(2026, 3, 13, 13, 0), occ.Select(o => o.StartTimeUtc));
        Assert.All(occ, o =>
            Assert.Equal(
                new TimeSpan(9, 0, 0),
                TimeZoneInfo.ConvertTimeFromUtc(o.StartTimeUtc, Ny).TimeOfDay));
    }

    [Fact]
    public void LegacyUtcAnchored_Weekly_DoesNotShiftAcrossDst()
    {
        // Same UTC start but TimeZoneId null → pure UTC stepping, so the
        // post-DST occurrence stays at 14:00 UTC (drifting to 10 AM local).
        var master = Master("FREQ=WEEKLY", Utc(2026, 2, 27, 14, 0), timeZoneId: null);
        var occ = Expand(master, Utc(2026, 2, 20), Utc(2026, 3, 20, 23, 59));

        Assert.Contains(Utc(2026, 3, 13, 14, 0), occ.Select(o => o.StartTimeUtc));
        Assert.DoesNotContain(Utc(2026, 3, 13, 13, 0), occ.Select(o => o.StartTimeUtc));
    }

    [Fact]
    public void SpringForward_InvalidLocalTime_ShiftsForward()
    {
        // 02:30 New York daily. On Mar 8 2026 that local time is in the
        // spring-forward gap and shifts forward to 03:30 EDT.
        var master = Master("FREQ=DAILY", Utc(2026, 3, 6, 7, 30), timeZoneId: NewYork);
        var occ = Expand(master, Utc(2026, 3, 6), Utc(2026, 3, 9, 23, 59));

        var mar8 = occ.Single(o =>
            TimeZoneInfo.ConvertTimeFromUtc(o.StartTimeUtc, Ny).Date == new DateTime(2026, 3, 8));
        Assert.Equal(
            new TimeSpan(3, 30, 0),
            TimeZoneInfo.ConvertTimeFromUtc(mar8.StartTimeUtc, Ny).TimeOfDay);
    }

    [Fact]
    public void FallBack_AmbiguousLocalTime_DoesNotThrowAndKeepsEveryDay()
    {
        // 01:30 New York daily across the fall-back overlap (Nov 1 2026).
        // ConvertTimeToUtc resolves the ambiguity to standard time; no day
        // is dropped and nothing throws.
        var master = Master("FREQ=DAILY", Utc(2026, 10, 30, 5, 30), timeZoneId: NewYork);
        var occ = Expand(master, Utc(2026, 10, 30), Utc(2026, 11, 2, 23, 59));

        Assert.Equal(4, occ.Count); // Oct 30, 31, Nov 1, Nov 2
    }

    [Fact]
    public void TzAware_CountAcrossSpringForward_YieldsContiguousDays()
    {
        // COUNT must not terminate early across the DST gap, and the gap
        // day must not be dropped: 10 daily occurrences over 10 contiguous
        // local dates spanning Mar 8.
        var master = Master("FREQ=DAILY;COUNT=10", Utc(2026, 3, 4, 7, 30), timeZoneId: NewYork);
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2027, 1, 1));

        Assert.Equal(10, occ.Count);

        var localDates = occ
            .Select(o => TimeZoneInfo.ConvertTimeFromUtc(o.StartTimeUtc, Ny).Date)
            .ToList();
        for (int i = 1; i < localDates.Count; i++)
            Assert.Equal(localDates[i - 1].AddDays(1), localDates[i]);
    }

    [Fact]
    public void TzAware_UntilAcrossSpringForward_ReachesUntilWithoutEarlyTermination()
    {
        // Invariant sibling to the COUNT case: UNTIL must not terminate the
        // walk early at the DST boundary either. A daily 09:00 New York
        // series with UNTIL a week past spring-forward (Mar 8) must emit
        // every scheduled day through UNTIL — the transition day included —
        // without cutting off at the gap. 09:00 never lands in the 02:00–
        // 03:00 gap, so every occurrence stays at 9 AM wall clock; the point
        // under test is that the tz-aware UNTIL gate (padded by TzWalkPad)
        // keeps walking past the boundary. UNTIL is the UTC of the final
        // intended occurrence (Mar 12 09:00 EDT = 13:00 UTC), inclusive.
        var master = Master(
            "FREQ=DAILY;UNTIL=20260312T130000Z",
            Utc(2026, 3, 4, 14, 0), // Mar 4 09:00 EST = 14:00 UTC
            timeZoneId: NewYork);
        var occ = Expand(master, Utc(2026, 1, 1), Utc(2027, 1, 1));

        var localDates = occ
            .Select(o => TimeZoneInfo.ConvertTimeFromUtc(o.StartTimeUtc, Ny).Date)
            .ToList();

        // Mar 4 through Mar 12 inclusive = 9 contiguous days, reaching UNTIL.
        Assert.Equal(9, occ.Count);
        Assert.Equal(new DateTime(2026, 3, 4), localDates[0]);
        Assert.Equal(new DateTime(2026, 3, 12), localDates[^1]);
        for (int i = 1; i < localDates.Count; i++)
            Assert.Equal(localDates[i - 1].AddDays(1), localDates[i]);

        // The transition day itself is present and still at 9 AM local.
        Assert.Contains(new DateTime(2026, 3, 8), localDates);
        Assert.All(occ, o =>
            Assert.Equal(
                new TimeSpan(9, 0, 0),
                TimeZoneInfo.ConvertTimeFromUtc(o.StartTimeUtc, Ny).TimeOfDay));
    }

    [Fact]
    public void BadTimeZoneId_FallsBackToLegacyUtcWalk_WithoutThrowing()
    {
        // Invariant #7: a TimeZoneId that does not resolve degrades to the
        // legacy UTC walk for that one series instead of throwing, so a
        // single malformed row (manual edit, import, restored backup) can
        // never take down the load. The write boundary prevents Chronicle
        // from producing such rows; the expander is the defense-in-depth
        // other end.
        //
        // Proof of "fell back to UTC, not tz-aware": the range spans the
        // spring-forward boundary. A tz-aware walk would shift the post-DST
        // occurrence from 14:00 to 13:00 UTC (wall-clock preserved); the
        // legacy UTC walk holds 14:00. The bad-tz expansion must match the
        // explicit null-tz walk exactly.
        var badTz = Master("FREQ=WEEKLY", Utc(2026, 2, 27, 14, 0),
            timeZoneId: "Definitely/NotAZone");
        var legacy = Master("FREQ=WEEKLY", Utc(2026, 2, 27, 14, 0),
            timeZoneId: null);

        var badOcc = Expand(badTz, Utc(2026, 2, 20), Utc(2026, 3, 20, 23, 59));
        var legacyOcc = Expand(legacy, Utc(2026, 2, 20), Utc(2026, 3, 20, 23, 59));

        Assert.Equal(
            legacyOcc.Select(o => o.StartTimeUtc),
            badOcc.Select(o => o.StartTimeUtc));

        // Concretely: post-DST occurrence stayed on the UTC clock (14:00),
        // did not shift to the wall-clock-preserving 13:00.
        Assert.Contains(Utc(2026, 3, 13, 14, 0), badOcc.Select(o => o.StartTimeUtc));
    }
}
