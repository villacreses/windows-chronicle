using Chronicle.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Layout;

/// <summary>
/// Layout result for one timed event: where it sits vertically (pixels) and how
/// it shares its overlap group horizontally (percentages, applied to the
/// measured timeline width by the renderer).
/// </summary>
internal sealed class PackedEvent
{
    public Event Event { get; set; } = null!;
    public double YPosition { get; set; }   // pixels from top
    public double Height { get; set; }      // pixels
    public double LeftPercent { get; set; } // 0-100% (column offset)
    public double WidthPercent { get; set; }// 0-100% (column width)
    public int ColumnIndex { get; set; }    // which column in overlap group
}

/// <summary>
/// Greedy interval packing for a single day's timed events — pure geometry,
/// no WinUI. Extracted from <c>TimelineRenderHelper</c> so the overlap layout
/// can be tested directly (see <c>.context/TESTING.md</c> "Timeline Packing
/// Extraction"). The renderer keeps the <c>UIElement</c> building; this owns
/// only the position/height/column math.
/// </summary>
internal static class TimelinePacker
{
    /// <summary>Hours in a day (timeline spans 00:00–23:00).</summary>
    private const int Hours = 24;

    /// <summary>
    /// Detect overlaps, pack events into columns, and compute each event's
    /// vertical position/height and horizontal column share.
    ///
    /// Greedy interval packing: events are sorted by start (longest first on
    /// ties) and grouped into <i>clusters</i> — a maximal run where each event
    /// starts before the cluster-so-far ends. Within a cluster, each event takes
    /// the first column whose previous event has already ended; if none is free a
    /// new column is opened. A cluster of N mutually overlapping events therefore
    /// splits the width into N side-by-side columns. This is the simplest layout
    /// that keeps every event visible and correct.
    ///
    /// Horizontal placement is returned as percentages (column offset / width)
    /// so the caller can turn them into pixels once the timeline column is
    /// measured. Vertical placement is in pixels (clamped to the timeline and
    /// trimmed so a block never spills past the end of the day).
    /// </summary>
    public static IList<PackedEvent> Pack(
        IList<Event> eventsForDay,
        double timelineHeight,
        TimeZoneInfo timeZone)
    {
        // Column assignment works on the timezone-invariant UTC instants.
        var sorted = eventsForDay
            .OrderBy(e => e.StartTimeUtc)
            .ThenByDescending(e => e.EndTimeUtc)
            .Select(e => new Working(e))
            .ToList();

        var result = new List<PackedEvent>();
        var cluster = new List<Working>();
        DateTime? clusterEnd = null;

        void Flush()
        {
            if (cluster.Count == 0)
                return;

            // Assign each event in the cluster the first column that is free at
            // its start time; open a new column when none is.
            var columnEnds = new List<DateTime>();
            foreach (var w in cluster)
            {
                bool placed = false;
                for (int ci = 0; ci < columnEnds.Count; ci++)
                {
                    if (columnEnds[ci] <= w.Start)
                    {
                        w.Col = ci;
                        columnEnds[ci] = w.End;
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    w.Col = columnEnds.Count;
                    columnEnds.Add(w.End);
                }
            }

            // Every event in the cluster shares the same column count, so the
            // whole cluster splits the width evenly.
            int cols = columnEnds.Count;
            foreach (var w in cluster)
            {
                var startLocal = ToLocal(w.Evt.StartTimeUtc, timeZone);
                var endLocal = ToLocal(w.Evt.EndTimeUtc, timeZone);

                var y = CalculateEventYPosition(startLocal.TimeOfDay, timelineHeight);
                var height = CalculateEventHeight(endLocal - startLocal, timelineHeight);

                y = Math.Clamp(y, 0, timelineHeight - 1);
                if (y + height > timelineHeight) height = timelineHeight - y;

                result.Add(new PackedEvent
                {
                    Event = w.Evt,
                    YPosition = y,
                    Height = height,
                    ColumnIndex = w.Col,
                    LeftPercent = (double)w.Col / cols * 100,
                    WidthPercent = 1.0 / cols * 100
                });
            }

            cluster.Clear();
            clusterEnd = null;
        }

        foreach (var w in sorted)
        {
            // A gap (this event starts at/after the cluster's running end) closes
            // the current cluster before starting a new one.
            if (clusterEnd is not null && w.Start >= clusterEnd)
                Flush();
            cluster.Add(w);
            clusterEnd = clusterEnd is null ? w.End : (w.End > clusterEnd ? w.End : clusterEnd);
        }
        Flush();

        return result;
    }

    /// <summary>Calculate vertical pixel position from time-of-day.</summary>
    private static double CalculateEventYPosition(TimeSpan startTime, double timelineHeight)
    {
        var hourHeight = timelineHeight / Hours;
        return (startTime.Hours + startTime.Minutes / 60.0) * hourHeight;
    }

    /// <summary>
    /// Calculate height in pixels from duration. Zero/negative durations fall
    /// back to a half-hour, and every block gets a 24px floor so very short
    /// events stay visible and clickable.
    /// </summary>
    private static double CalculateEventHeight(TimeSpan duration, double timelineHeight)
    {
        var hourHeight = timelineHeight / Hours;
        var durationHours = duration.TotalHours;
        if (durationHours <= 0) durationHours = 0.5;
        return Math.Max(durationHours * hourHeight, 24);
    }

    /// <summary>
    /// Converts a stored UTC timestamp to wall-clock time in the given zone.
    /// Matches <c>DateTime.ToLocalTime()</c> when <paramref name="timeZone"/> is
    /// the local zone, regardless of the value's <see cref="DateTimeKind"/>.
    /// </summary>
    private static DateTime ToLocal(DateTime utc, TimeZoneInfo timeZone)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone);

    /// <summary>Working column-assignment state during packing (UTC instants).</summary>
    private sealed class Working
    {
        public Working(Event evt)
        {
            Evt = evt;
            Start = evt.StartTimeUtc;
            End = evt.EndTimeUtc;
        }

        public Event Evt { get; }
        public DateTime Start { get; }
        public DateTime End { get; }
        public int Col { get; set; }
    }
}
