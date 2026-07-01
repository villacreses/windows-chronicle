using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
///   <item>Owns the day's <i>layout</i> — splits the host into an all-day band
///   (shown only when the day has all-day events) and a scrollable timeline
///   row. The <see cref="ScrollViewer"/> is held as a persistent field —
///   created on first <see cref="Render"/>, reused thereafter. Only its
///   <see cref="ScrollViewer.Content"/> is swapped on subsequent renders, so
///   <see cref="ScrollViewer.VerticalOffset"/> survives every refresh. The
///   renderer never moves the scroll position; the user's offset is preserved
///   as-is.</item>
///   <item>Renders the all-day band directly (label + event chips).</item>
///   <item>Delegates the 24-hour timeline to
///   <see cref="TimelineRenderHelper.BuildDayTimeline"/>: gutter, gridlines,
///   now-line, and timed event blocks with overlap packing.</item>
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
    private readonly Grid _host;
    private readonly ICalendarInteractionHost _interactions;

    // Persistent visuals created on first Render() and reused thereafter.
    // Keeping the ScrollViewer instance is what preserves the user's scroll
    // offset across re-renders — a fresh ScrollViewer would start at 0.
    private ScrollViewer? _scroll;
    private Border? _allDayBand;

    public DayViewRenderer(Grid host, ICalendarInteractionHost interactions)
    {
        _host = host;
        _interactions = interactions;
    }

    /// <summary>
    /// Renders <paramref name="dayEvents"/> (already filtered to visible
    /// calendars) for <paramref name="selectedDate"/>. Event taps and
    /// empty-slot taps route through <see cref="ICalendarInteractionHost"/>.
    /// </summary>
    public void Render(
        DateTime selectedDate,
        List<Event> dayEvents,
        List<Calendar> calendars)
    {
        var allDay = dayEvents.Where(e => e.IsAllDay).ToList();
        var timed = dayEvents.Where(e => !e.IsAllDay).ToList();

        // First-time init: lay out the host (Auto row + Star row) and add the
        // persistent ScrollViewer. After this, _scroll stays in _host.Children
        // for the lifetime of the renderer so its VerticalOffset survives
        // every subsequent Render().
        if (_scroll is null)
        {
            _host.Children.Clear();
            _host.ColumnDefinitions.Clear();
            _host.RowDefinitions.Clear();
            _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(_scroll, 1);
            _host.Children.Add(_scroll);

            // One-shot starting offset: seed the scroll to ~7am on first mount
            // so the user lands at a sensible time of day instead of midnight.
            // Deferred to Low priority so the first Content swap below has been
            // measured/arranged before ChangeView runs (calling it before layout
            // silently no-ops). After this, the renderer never moves the scroll
            // position again — subsequent Render() calls only swap Content and
            // VerticalOffset is preserved.
            var scroll = _scroll;
            scroll.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                scroll.ChangeView(null, 7 * TimelineRenderHelper.HourHeight, null, disableAnimation: true));
        }

        // Row 0: replace the all-day band. The band is conditional — present
        // only when the day has all-day events — so we may add, remove, or
        // swap it across renders. The ScrollViewer below is untouched, so its
        // scroll position holds.
        if (_allDayBand is not null)
        {
            _host.Children.Remove(_allDayBand);
            _allDayBand = null;
        }
        if (allDay.Count > 0)
        {
            var band = BuildAllDayBand(allDay, calendars, _interactions);
            Grid.SetRow(band, 0);
            _host.Children.Add(band);
            _allDayBand = band;
        }

        // Swap the timeline into the persistent ScrollViewer. Setting Content
        // doesn't reset VerticalOffset; the user stays where they were
        // looking. No programmatic scroll ever.
        _scroll.Content = TimelineRenderHelper.BuildDayTimeline(
            selectedDate,
            timed,
            calendars,
            TimeZoneInfo.Local,
            _interactions,
            time => _interactions.OnTimeSlotCreateRequested(selectedDate, time));
    }

    // ── All-day band ──────────────────────────────────────────────────────

    private static Border BuildAllDayBand(
        List<Event> allDay, List<Calendar> calendars, ICalendarInteractionHost interactions)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 8, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineRenderHelper.GutterWidth) });
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
            chips.Children.Add(CalendarRenderHelper.CreateEventChip(evt, calendars, evt.Title, interactions));
        Grid.SetColumn(chips, 1);
        grid.Children.Add(chips);

        return new Border
        {
            Child = grid,
            BorderBrush = new SolidColorBrush(Theme.Hairline2),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }
}
