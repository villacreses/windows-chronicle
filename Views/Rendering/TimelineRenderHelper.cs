using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Builds a single day's <i>timed</i> timeline — the part below the all-day
/// band: a time gutter (00:00–23:00 hour labels), solid hour lines with dashed
/// half-hour lines, an optional current-time indicator, and timed event blocks
/// positioned by their start/end with overlapping events packed into
/// side-by-side columns.
///
/// This logic was extracted from <see cref="DayViewRenderer"/> so it can be
/// reused: Week View will render seven of these timelines side by side. The
/// helper is stateless (static) — each call returns a fresh, self-contained
/// timeline element that wires up its own horizontal layout on size change and
/// reports event taps / empty-slot taps back through callbacks. It owns
/// no navigation or selection state.
///
/// All-day events are <b>not</b> handled here; the caller filters them out and
/// renders its own all-day band. Shared chip/colors come from
/// <see cref="ColorHelper"/>; colors from <see cref="Theme"/>.
/// </summary>
internal static class TimelineRenderHelper
{
    /// <summary>Pixel height of one hour row. Timeline geometry is derived from this.</summary>
    public const double HourHeight = 56;

    /// <summary>Width of the left time-label gutter.</summary>
    public const double GutterWidth = 56;

    /// <summary>Hours in a day (timeline spans 00:00–23:00).</summary>
    public const int Hours = 24;

    /// <summary>Full timeline height (00:00 through end of 23:00).</summary>
    public const double TotalHeight = Hours * HourHeight;

    /// <summary>
    /// Returns a standalone time gutter for use when one gutter is shared across
    /// multiple day columns (e.g., Week View puts one gutter on the far left with
    /// seven <see cref="BuildDayColumnContent"/> results to its right).
    /// </summary>
    public static FrameworkElement BuildSharedGutter()
        => RenderTimeGutter(GutterWidth, TotalHeight);

    /// <summary>
    /// Builds just the day column body — gridlines, now-line, and timed event
    /// blocks — without the gutter wrapper. Used by <c>WeekViewRenderer</c>,
    /// which supplies a single shared gutter for all seven columns. Event-block
    /// taps route through <paramref name="host"/> directly via
    /// <see cref="EventTapTarget"/>; no tap-handler parameter is threaded
    /// through. <paramref name="onCreateAt"/> fires when an empty time slot is
    /// tapped, with the slot's start hour.
    /// </summary>
    public static FrameworkElement BuildDayColumnContent(
        DateTime dayDate,
        IList<Event> eventsForDay,
        IList<Calendar> calendars,
        TimeZoneInfo timeZone,
        ICalendarInteractionHost host,
        Action<TimeSpan> onCreateAt)
    {
        // Transparent background makes empty areas hit-testable so
        // tap-to-create works between events.
        var dayColumn = new Grid
        {
            Height = TotalHeight,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        dayColumn.Children.Add(RenderGridlines(TotalHeight));

        var nowLine = RenderNowLine(dayDate, TotalHeight, timeZone);
        if (nowLine is not null)
            dayColumn.Children.Add(nowLine);

        // Build the event blocks now (vertical position + height are known), but
        // defer horizontal position/width until the canvas has a measured width —
        // Canvas children need explicit pixel widths, and overlapping events split
        // the available width evenly.
        var canvas = new Canvas();
        var placed = new List<(Border Block, PackedEvent Packed)>();
        foreach (var pe in PackOverlappingEvents(eventsForDay, TotalHeight, timeZone))
        {
            var block = RenderEventBlock(pe, calendars, timeZone, host);
            Canvas.SetTop(block, pe.YPosition);
            block.Height = pe.Height - 2;
            canvas.Children.Add(block);
            placed.Add((block, pe));
        }
        canvas.SizeChanged += (s, e) => LayoutEventsHorizontally(canvas, placed);
        dayColumn.Children.Add(canvas);

        // Single tap on empty timeline space creates an event at that hour.
        // Event blocks mark their own taps handled, so this only fires on gaps.
        dayColumn.Tapped += (s, e) =>
        {
            var y = e.GetPosition(dayColumn).Y;
            int hour = Math.Clamp((int)(y / HourHeight), 0, Hours - 1);
            onCreateAt(TimeSpan.FromHours(hour));
        };

        return dayColumn;
    }

    /// <summary>
    /// Main entry point for Day View: renders a complete timeline (gutter +
    /// gridlines + now-line + timed events) for <paramref name="dayDate"/>.
    /// <paramref name="eventsForDay"/> must contain only timed events (all-day
    /// events are handled by the caller). Event-block taps route through
    /// <paramref name="host"/> directly via <see cref="EventTapTarget"/>.
    /// <paramref name="onCreateAt"/> fires when an empty time slot is tapped,
    /// with the slot's start hour.
    /// </summary>
    public static UIElement BuildDayTimeline(
        DateTime dayDate,
        IList<Event> eventsForDay,
        IList<Calendar> calendars,
        TimeZoneInfo timeZone,
        ICalendarInteractionHost host,
        Action<TimeSpan> onCreateAt)
    {
        var timeline = new Grid { Height = TotalHeight };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutter = RenderTimeGutter(GutterWidth, TotalHeight);
        Grid.SetColumn(gutter, 0);
        timeline.Children.Add(gutter);

        var dayCol = BuildDayColumnContent(dayDate, eventsForDay, calendars, timeZone, host, onCreateAt);
        Grid.SetColumn(dayCol, 1);
        timeline.Children.Add(dayCol);

        return timeline;
    }

    // ── Geometry ──────────────────────────────────────────────────────────

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
    private static IList<PackedEvent> PackOverlappingEvents(
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

    /// <summary>
    /// Positions each event block horizontally once the canvas has a measured
    /// width: each block's column offset/width percentage is turned into pixels,
    /// with a small inset so adjacent columns don't touch.
    /// </summary>
    private static void LayoutEventsHorizontally(
        Canvas canvas,
        IReadOnlyList<(Border Block, PackedEvent Packed)> placed)
    {
        var width = canvas.ActualWidth;
        if (width <= 0)
            return;

        foreach (var (block, pe) in placed)
        {
            Canvas.SetLeft(block, width * pe.LeftPercent / 100 + 2);
            block.Width = Math.Max(width * pe.WidthPercent / 100 - 4, 0);
        }
    }

    // ── Chrome (gutter / gridlines / now-line) ─────────────────────────────

    /// <summary>Render the time gutter (right-aligned hour labels).</summary>
    private static FrameworkElement RenderTimeGutter(double gutterWidth, double timelineHeight)
    {
        var hourHeight = timelineHeight / Hours;
        var gutter = new Canvas { Width = gutterWidth, Height = timelineHeight };
        for (int h = 0; h < Hours; h++)
        {
            var label = new TextBlock
            {
                Text = $"{h:00}:00",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Theme.Text3),
                Width = gutterWidth - 8,
                TextAlignment = TextAlignment.Right
            };
            // Sit the label on its hour line (except 00:00, which hugs the top).
            Canvas.SetTop(label, h == 0 ? 1 : h * hourHeight - 7);
            Canvas.SetLeft(label, 0);
            gutter.Children.Add(label);
        }
        return gutter;
    }

    /// <summary>Render solid hour lines with dashed half-hour lines.</summary>
    private static UIElement RenderGridlines(double timelineHeight)
    {
        var hourHeight = timelineHeight / Hours;
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (int h = 0; h < Hours; h++)
        {
            var cell = new Grid
            {
                Height = hourHeight,
                BorderBrush = new SolidColorBrush(Theme.Hairline2),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };

            // Dashed half-hour line at the vertical centre of the hour cell.
            var halfHour = new Rectangle
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Stroke = new SolidColorBrush(Theme.Hairline),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            cell.Children.Add(halfHour);

            panel.Children.Add(cell);
        }
        return panel;
    }

    /// <summary>
    /// Render the "now" indicator at the current time. Returns null unless
    /// <paramref name="dayDate"/> is today in <paramref name="timeZone"/> (the
    /// line is only meaningful on the day actually in progress).
    /// </summary>
    private static UIElement? RenderNowLine(DateTime dayDate, double timelineHeight, TimeZoneInfo timeZone)
    {
        var now = ToLocal(DateTime.UtcNow, timeZone);
        if (!DateHelpers.IsSameDay(dayDate, now))
            return null;

        var hourHeight = timelineHeight / Hours;
        var y = (now.Hour + now.Minute / 60.0) * hourHeight;

        return new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Theme.Danger),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, y, 0, 0),
            IsHitTestVisible = false
        };
    }

    // ── Timed event block ──────────────────────────────────────────────────

    /// <summary>
    /// Builds one timed-event block (calendar-tinted, with a title and — when
    /// tall enough — a start/end time line). Vertical position/height are applied
    /// by the caller; horizontal position is applied later in
    /// <see cref="LayoutEventsHorizontally"/>.
    /// </summary>
    private static Border RenderEventBlock(
        PackedEvent pe,
        IList<Calendar> calendars,
        TimeZoneInfo timeZone,
        ICalendarInteractionHost host)
    {
        var evt = pe.Event;
        var color = ColorHelper.ResolveCalendarColor(calendars, evt.CalendarId);

        var startLocal = ToLocal(evt.StartTimeUtc, timeZone);
        var endLocal = ToLocal(evt.EndTimeUtc, timeZone);

        var content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1 };
        content.Children.Add(new TextBlock
        {
            Text = evt.Title,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(ColorHelper.LightenForText(color))
        });
        if (pe.Height > 34)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"{startLocal:h:mm tt} – {endLocal:h:mm tt}",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(ColorHelper.LightenForText(color, 0.4))
            });
        }

        var block = new Border
        {
            Tag = new EventTapTarget(evt, host),
            Child = content,
            Background = new SolidColorBrush(ColorHelper.Soften(color)),
            BorderBrush = new SolidColorBrush(color),
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 3, 7, 3)
        };

        block.Tapped += EventTapTarget.OnTapped;
        block.DoubleTapped += EventTapTarget.MarkHandled;

        return block;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

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

/// <summary>
/// Layout result for one timed event: where it sits vertically (pixels) and how
/// it shares its overlap group horizontally (percentages, applied to the
/// measured timeline width).
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
