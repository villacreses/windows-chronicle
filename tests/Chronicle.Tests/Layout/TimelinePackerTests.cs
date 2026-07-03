using System;
using System.Linq;
using Chronicle.Layout;
using Chronicle.Models;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Layout;

/// <summary>
/// Layer 5 — the pure overlap-packing geometry extracted into
/// <see cref="TimelinePacker"/>. Every case pins <see cref="TimeZoneInfo.Utc"/>
/// so wall-clock equals the stored UTC, and a 2400px timeline (100px/hour) so
/// positions and heights are exact round numbers. Packing correctness is
/// visual and easy to reintroduce by hand; these lock it down.
/// </summary>
public sealed class TimelinePackerTests
{
    // 100px per hour keeps the arithmetic clean (9:00 → y=900, 1h → 100px).
    private const double Height = 2400;

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static Event Ev(DateTime startUtc, DateTime endUtc)
        => StandaloneEvent(Guid.NewGuid(), startUtc: startUtc, duration: endUtc - startUtc);

    [Fact]
    public void Pack_NonOverlappingEvents_EachFullWidthInOneColumn()
    {
        // A clear gap between the two: they form separate single-column clusters.
        var events = new[]
        {
            Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 10, 0)),
            Ev(Utc(2026, 6, 1, 11, 0), Utc(2026, 6, 1, 12, 0)),
        };

        var result = TimelinePacker.Pack(events, Height, Utc);

        Assert.Equal(2, result.Count);
        Assert.All(result, pe =>
        {
            Assert.Equal(0, pe.ColumnIndex);
            Assert.Equal(0, pe.LeftPercent, 3);
            Assert.Equal(100, pe.WidthPercent, 3);
        });
    }

    [Fact]
    public void Pack_AdjacentEventsTouchingAtBoundary_DoNotOverlap()
    {
        // B starts exactly when A ends — treated as non-overlapping (each full width).
        var a = Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 10, 0));
        var b = Ev(Utc(2026, 6, 1, 10, 0), Utc(2026, 6, 1, 11, 0));

        var result = TimelinePacker.Pack(new[] { a, b }, Height, Utc);

        Assert.All(result, pe => Assert.Equal(100, pe.WidthPercent, 3));
    }

    [Fact]
    public void Pack_TwoOverlappingEvents_SplitIntoTwoHalfWidthColumns()
    {
        var a = Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 11, 0));
        var b = Ev(Utc(2026, 6, 1, 10, 0), Utc(2026, 6, 1, 12, 0));

        var result = TimelinePacker.Pack(new[] { a, b }, Height, Utc);

        var pa = result.Single(p => ReferenceEquals(p.Event, a));
        var pb = result.Single(p => ReferenceEquals(p.Event, b));

        Assert.Equal(0, pa.ColumnIndex);
        Assert.Equal(0, pa.LeftPercent, 3);
        Assert.Equal(50, pa.WidthPercent, 3);

        Assert.Equal(1, pb.ColumnIndex);
        Assert.Equal(50, pb.LeftPercent, 3);
        Assert.Equal(50, pb.WidthPercent, 3);
    }

    [Fact]
    public void Pack_ThreeMutuallyOverlappingEvents_SplitIntoThirds()
    {
        var events = new[]
        {
            Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 12, 0)),
            Ev(Utc(2026, 6, 1, 9, 30), Utc(2026, 6, 1, 11, 0)),
            Ev(Utc(2026, 6, 1, 10, 0), Utc(2026, 6, 1, 11, 30)),
        };

        var result = TimelinePacker.Pack(events, Height, Utc);

        Assert.Equal(new[] { 0, 1, 2 }, result.Select(p => p.ColumnIndex).OrderBy(x => x));
        Assert.All(result, pe =>
        {
            Assert.Equal(100.0 / 3, pe.WidthPercent, 3);
            Assert.Equal(pe.ColumnIndex * (100.0 / 3), pe.LeftPercent, 3);
        });
    }

    [Fact]
    public void Pack_ColumnIsReusedAfterItsEventEnds()
    {
        // A spans the whole cluster; B ends early, freeing its column for C. The
        // cluster therefore needs only two columns, not three.
        var a = Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 12, 0));
        var b = Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 9, 30));
        var c = Ev(Utc(2026, 6, 1, 9, 45), Utc(2026, 6, 1, 10, 15));

        var result = TimelinePacker.Pack(new[] { a, b, c }, Height, Utc);

        var pc = result.Single(p => ReferenceEquals(p.Event, c));
        Assert.Equal(1, pc.ColumnIndex); // reused B's freed column
        Assert.All(result, pe => Assert.Equal(50, pe.WidthPercent, 3)); // two columns
    }

    [Fact]
    public void Pack_ComputesYPositionAndHeightFromLocalTime()
    {
        var pe = TimelinePacker
            .Pack(new[] { Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 10, 0)) }, Height, Utc)
            .Single();

        Assert.Equal(900, pe.YPosition, 3); // 9 hours * 100px
        Assert.Equal(100, pe.Height, 3);    // 1 hour * 100px
    }

    [Fact]
    public void Pack_ShortEventHeightFlooredTo24_ZeroDurationFallsBackToHalfHour()
    {
        // 10 minutes → 16.67px, floored to the 24px minimum.
        var shortEvent = TimelinePacker
            .Pack(new[] { Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 9, 10)) }, Height, Utc)
            .Single();
        Assert.Equal(24, shortEvent.Height, 3);

        // Zero duration → half-hour fallback (50px at 100px/hour), above the floor.
        var zeroDuration = TimelinePacker
            .Pack(new[] { Ev(Utc(2026, 6, 1, 9, 0), Utc(2026, 6, 1, 9, 0)) }, Height, Utc)
            .Single();
        Assert.Equal(50, zeroDuration.Height, 3);
    }

    [Fact]
    public void Pack_EventRunningPastEndOfDay_HeightTrimmedToTimeline()
    {
        // Starts at 23:30 and runs an hour; the block is trimmed so it never
        // spills past the bottom of the timeline.
        var pe = TimelinePacker
            .Pack(new[] { Ev(Utc(2026, 6, 1, 23, 30), Utc(2026, 6, 2, 0, 30)) }, Height, Utc)
            .Single();

        Assert.Equal(2350, pe.YPosition, 3);      // 23.5 * 100px
        Assert.Equal(Height - 2350, pe.Height, 3); // trimmed to the remaining 50px
    }
}
