using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Renders the Day View into a host <see cref="Grid"/>: an optional all-day
/// band above a scrollable 24-hour timeline for a single day.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Owns the day's <i>layout</i> — an all-day band (shown only when the
///   day has all-day events), a 56px time gutter labelled 00:00–23:00, solid
///   hour lines with dashed half-hour lines, and timed events positioned by
///   their start/end with overlapping events packed into side-by-side columns.</item>
///   <item>Draws a current-time indicator when the rendered day is today, and
///   auto-scrolls to ~7am (or the first event).</item>
///   <item>Reports event clicks and empty-slot double-clicks back to the caller
///   via callbacks; it owns no navigation or event state and derives the day
///   entirely from the date handed to <see cref="Render"/>.</item>
/// </list>
///
/// Shared chip/colors come from <see cref="CalendarRenderHelper"/> /
/// <see cref="ColorHelper"/>; colors from <see cref="Theme"/>.
/// </summary>
internal sealed class DayViewRenderer
{
    private const double HourHeight = 56;
    private const double GutterWidth = 56;
    private const int Hours = 24;
    private const double TotalHeight = Hours * HourHeight;

    private readonly Grid _host;

    // Kept so the events can be (re)positioned once the timeline column has a
    // measured width (Canvas children need explicit pixel widths).
    private Canvas? _eventsCanvas;
    private readonly List<PlacedEvent> _placed = new();

    public DayViewRenderer(Grid host)
    {
        _host = host;
    }

    /// <summary>
    /// Renders <paramref name="dayEvents"/> (already filtered to visible
    /// calendars) for <paramref name="selectedDate"/>.
    /// <paramref name="onEventClicked"/> fires when an event is tapped (opens
    /// the popover). <paramref name="onCreateAt"/> fires when an empty time
    /// slot is double-tapped, with the slot's start time.
    /// </summary>
    public void Render(
        DateTime selectedDate,
        List<Event> dayEvents,
        List<Calendar> calendars,
        Action<Event, FrameworkElement> onEventClicked,
        Action<TimeSpan> onCreateAt)
    {
        _placed.Clear();
        _eventsCanvas = null;

        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allDay = dayEvents.Where(e => e.IsAllDay).ToList();
        var timed = dayEvents.Where(e => !e.IsAllDay).ToList();

        if (allDay.Count > 0)
        {
            var band = BuildAllDayBand(allDay, calendars, onEventClicked);
            Grid.SetRow(band, 0);
            _host.Children.Add(band);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = BuildTimeline(selectedDate, timed, calendars, onEventClicked, onCreateAt)
        };
        Grid.SetRow(scroll, 1);
        _host.Children.Add(scroll);

        // Auto-scroll to ~7am, or earlier if the first event starts before then.
        double targetHour = 7;
        if (timed.Count > 0)
            targetHour = Math.Min(targetHour, timed.Min(e => e.StartTimeUtc.ToLocalTime().Hour));
        var targetY = Math.Max(0, targetHour) * HourHeight;
        scroll.DispatcherQueue.TryEnqueue(() => scroll.ChangeView(null, targetY, null, disableAnimation: true));
    }

    // ── All-day band ──────────────────────────────────────────────────────

    private static Border BuildAllDayBand(
        List<Event> allDay, List<Calendar> calendars, Action<Event, FrameworkElement> onEventClicked)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 8, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = "all-day",
            FontSize = 11,
            Foreground = new SolidColorBrush(Theme.Text3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 8, 0)
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var chips = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        foreach (var evt in allDay)
            chips.Children.Add(CalendarRenderHelper.CreateEventChip(evt, calendars, evt.Title, onEventClicked));
        Grid.SetColumn(chips, 1);
        grid.Children.Add(chips);

        return new Border
        {
            Child = grid,
            BorderBrush = new SolidColorBrush(Theme.Hairline2),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    // ── Timeline ──────────────────────────────────────────────────────────

    private Grid BuildTimeline(
        DateTime selectedDate,
        List<Event> timed,
        List<Calendar> calendars,
        Action<Event, FrameworkElement> onEventClicked,
        Action<TimeSpan> onCreateAt)
    {
        var timeline = new Grid { Height = TotalHeight };
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GutterWidth) });
        timeline.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutter = BuildGutter();
        Grid.SetColumn(gutter, 0);
        timeline.Children.Add(gutter);

        var dayColumn = new Grid
        {
            // A transparent background makes empty areas hit-testable so
            // double-tap-to-create works between events.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };
        dayColumn.Children.Add(BuildHourLines());

        if (DateHelpers.IsSameDay(selectedDate, DateTime.Now))
            dayColumn.Children.Add(BuildNowLine());

        var eventsCanvas = new Canvas();
        eventsCanvas.SizeChanged += (s, e) => LayoutEvents();
        _eventsCanvas = eventsCanvas;
        BuildEventBlocks(timed, calendars, onEventClicked, eventsCanvas);
        dayColumn.Children.Add(eventsCanvas);

        dayColumn.DoubleTapped += (s, e) =>
        {
            var y = e.GetPosition(dayColumn).Y;
            int hour = Math.Clamp((int)(y / HourHeight), 0, Hours - 1);
            onCreateAt(TimeSpan.FromHours(hour));
        };

        Grid.SetColumn(dayColumn, 1);
        timeline.Children.Add(dayColumn);

        return timeline;
    }

    private static Canvas BuildGutter()
    {
        var gutter = new Canvas { Width = GutterWidth, Height = TotalHeight };
        for (int h = 0; h < Hours; h++)
        {
            var label = new TextBlock
            {
                Text = $"{h:00}:00",
                FontSize = 10.5,
                Foreground = new SolidColorBrush(Theme.Text3),
                Width = GutterWidth - 8,
                TextAlignment = TextAlignment.Right
            };
            // Sit the label on its hour line (except 00:00, which hugs the top).
            Canvas.SetTop(label, h == 0 ? 1 : h * HourHeight - 7);
            Canvas.SetLeft(label, 0);
            gutter.Children.Add(label);
        }
        return gutter;
    }

    private static StackPanel BuildHourLines()
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        for (int h = 0; h < Hours; h++)
        {
            var cell = new Grid
            {
                Height = HourHeight,
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

    private static Border BuildNowLine()
    {
        var now = DateTime.Now;
        var y = (now.Hour + now.Minute / 60.0) * HourHeight;

        var line = new Border
        {
            Height = 2,
            Background = new SolidColorBrush(Theme.Danger),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, y, 0, 0),
            IsHitTestVisible = false
        };
        return line;
    }

    // ── Timed event blocks ────────────────────────────────────────────────

    private void BuildEventBlocks(
        List<Event> timed,
        List<Calendar> calendars,
        Action<Event, FrameworkElement> onEventClicked,
        Canvas canvas)
    {
        foreach (var placed in PackDay(timed))
        {
            var evt = placed.Evt;
            var color = ColorHelper.ResolveCalendarColor(calendars, evt.CalendarId);

            var startLocal = evt.StartTimeUtc.ToLocalTime();
            var endLocal = evt.EndTimeUtc.ToLocalTime();

            var top = (startLocal.Hour + startLocal.Minute / 60.0) * HourHeight;
            var durationHours = (endLocal - startLocal).TotalHours;
            if (durationHours <= 0) durationHours = 0.5;
            var height = Math.Max(durationHours * HourHeight, 24);

            top = Math.Clamp(top, 0, TotalHeight - 1);
            if (top + height > TotalHeight) height = TotalHeight - top;

            var content = new StackPanel { Orientation = Orientation.Vertical, Spacing = 1 };
            content.Children.Add(new TextBlock
            {
                Text = evt.Title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = new SolidColorBrush(ColorHelper.LightenForText(color))
            });
            if (height > 34)
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
                Child = content,
                Background = new SolidColorBrush(ColorHelper.Soften(color)),
                BorderBrush = new SolidColorBrush(color),
                BorderThickness = new Thickness(3, 0, 0, 0),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 3, 7, 3)
            };

            var capturedEvt = evt;
            block.Tapped += (s, e) =>
            {
                e.Handled = true;
                onEventClicked(capturedEvt, block);
            };
            block.DoubleTapped += (s, e) => e.Handled = true;

            Canvas.SetTop(block, top);
            block.Height = height - 2;
            canvas.Children.Add(block);

            _placed.Add(new PlacedEvent(block, placed.Col, placed.Cols));
        }
    }

    /// <summary>
    /// Positions each event block horizontally once the canvas has a measured
    /// width (overlapping events split the column width evenly).
    /// </summary>
    private void LayoutEvents()
    {
        if (_eventsCanvas is null)
            return;

        var width = _eventsCanvas.ActualWidth;
        if (width <= 0)
            return;

        foreach (var p in _placed)
        {
            var colWidth = width / p.Cols;
            Canvas.SetLeft(p.Block, p.Col * colWidth + 2);
            p.Block.Width = Math.Max(colWidth - 4, 0);
        }
    }

    /// <summary>
    /// Greedy interval packing: groups overlapping events into a cluster and
    /// assigns each the first free column, so a cluster of N mutually
    /// overlapping events splits the width into N side-by-side columns. This is
    /// the simplest layout that keeps every event visible and correct.
    /// </summary>
    private static List<Packed> PackDay(List<Event> timed)
    {
        var sorted = timed
            .OrderBy(e => e.StartTimeUtc)
            .ThenByDescending(e => e.EndTimeUtc)
            .ToList();

        var result = new List<Packed>();
        var cluster = new List<Packed>();
        DateTime? clusterEnd = null;

        void Flush()
        {
            if (cluster.Count == 0)
                return;

            var columnEnds = new List<DateTime>();
            foreach (var p in cluster)
            {
                bool placed = false;
                for (int ci = 0; ci < columnEnds.Count; ci++)
                {
                    if (columnEnds[ci] <= p.Start)
                    {
                        p.Col = ci;
                        columnEnds[ci] = p.End;
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                {
                    p.Col = columnEnds.Count;
                    columnEnds.Add(p.End);
                }
            }
            foreach (var p in cluster)
            {
                p.Cols = columnEnds.Count;
                result.Add(p);
            }
            cluster.Clear();
            clusterEnd = null;
        }

        foreach (var evt in sorted)
        {
            var p = new Packed(evt, evt.StartTimeUtc, evt.EndTimeUtc);
            if (clusterEnd is not null && p.Start >= clusterEnd)
                Flush();
            cluster.Add(p);
            clusterEnd = clusterEnd is null ? p.End : (p.End > clusterEnd ? p.End : clusterEnd);
        }
        Flush();

        return result;
    }

    private sealed class Packed
    {
        public Packed(Event evt, DateTime start, DateTime end)
        {
            Evt = evt;
            Start = start;
            End = end;
        }

        public Event Evt { get; }
        public DateTime Start { get; }
        public DateTime End { get; }
        public int Col { get; set; }
        public int Cols { get; set; } = 1;
    }

    private sealed record PlacedEvent(Border Block, int Col, int Cols);
}
