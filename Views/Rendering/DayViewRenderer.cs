using Chronicle.Helpers;
using Chronicle.Models;
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
///   row, auto-scrolled to ~7am or the first event on load.</item>
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
    // Cached method-group conversion of _interactions.OnEventClicked, passed
    // to static chip/timeline helpers without per-call delegate allocation.
    private readonly Action<Event, FrameworkElement> _onEventClicked;

    public DayViewRenderer(Grid host, ICalendarInteractionHost interactions)
    {
        _host = host;
        _interactions = interactions;
        _onEventClicked = interactions.OnEventClicked;
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
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var allDay = dayEvents.Where(e => e.IsAllDay).ToList();
        var timed = dayEvents.Where(e => !e.IsAllDay).ToList();

        if (allDay.Count > 0)
        {
            var band = BuildAllDayBand(allDay, calendars, _onEventClicked);
            Grid.SetRow(band, 0);
            _host.Children.Add(band);
        }

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = TimelineRenderHelper.BuildDayTimeline(
                selectedDate,
                timed,
                calendars,
                TimeZoneInfo.Local,
                _onEventClicked,
                time => _interactions.OnTimeSlotCreateRequested(selectedDate, time))
        };
        Grid.SetRow(scroll, 1);
        _host.Children.Add(scroll);

        // Auto-scroll to ~7am, or earlier if the first event starts before then.
        double targetHour = 7;
        if (timed.Count > 0)
            targetHour = Math.Min(targetHour, timed.Min(e => e.StartTimeUtc.ToLocalTime().Hour));
        var targetY = Math.Max(0, targetHour) * TimelineRenderHelper.HourHeight;
        scroll.DispatcherQueue.TryEnqueue(() => scroll.ChangeView(null, targetY, null, disableAnimation: true));
    }

    // ── All-day band ──────────────────────────────────────────────────────

    private static Border BuildAllDayBand(
        List<Event> allDay, List<Calendar> calendars, Action<Event, FrameworkElement> onEventClicked)
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
}
