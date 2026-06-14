using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Renders the Week View into a host <see cref="Grid"/>: seven Sunday→Saturday
/// day columns derived from the selected date.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Owns the week's <i>layout</i> — seven star-width columns, and each
///   column's header (weekday abbreviation + circular date badge) and a
///   scrollable list of that day's filled event-pill chips.</item>
///   <item>Highlights today (accent-filled badge) and the selected day (soft
///   tint + accent ring on the column), and exposes
///   <see cref="UpdateSelectedDate"/> for incremental selection.</item>
///   <item>Reports day selection, day activation, and event clicks back to the
///   caller via callbacks; it owns no navigation or event state.</item>
/// </list>
///
/// Week geometry comes from <see cref="DateHelpers.BuildWeek"/> and events from
/// the shared <c>_eventsByDate</c> store; shared visuals come from
/// <see cref="CalendarRenderHelper"/> and colors from <see cref="Theme"/>.
/// </summary>
internal sealed class WeekViewRenderer
{
    private readonly Grid _weekGrid;
    private readonly Dictionary<DateTime, Border> _dayColumns = new();
    private readonly Dictionary<DateTime, Border> _dayNumberCircles = new();
    private readonly Dictionary<DateTime, TextBlock> _dayNumberBlocks = new();

    private DateTime _selectedDate;

    public WeekViewRenderer(Grid weekGrid)
    {
        _weekGrid = weekGrid;
    }

    /// <summary>
    /// Renders the week containing <paramref name="selectedDate"/>.
    /// <paramref name="onDaySelected"/> fires on a single tap of a column
    /// (selects the day); <paramref name="onDayActivated"/> on a double tap
    /// (creates an event); <paramref name="onEventClicked"/> when an event
    /// chip is tapped (opens the popover, anchored to the chip).
    /// </summary>
    public void Render(
        DateTime selectedDate,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDaySelected,
        Action<DateTime> onDayActivated,
        Action<Event, FrameworkElement> onEventClicked)
    {
        _selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _dayColumns.Clear();
        _dayNumberCircles.Clear();
        _dayNumberBlocks.Clear();

        _weekGrid.Children.Clear();
        _weekGrid.ColumnDefinitions.Clear();
        _weekGrid.RowDefinitions.Clear();

        for (int i = 0; i < 7; i++)
            _weekGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _weekGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        int column = 0;
        foreach (var dayDate in DateHelpers.BuildWeek(selectedDate))
        {
            var cell = CreateDayColumn(
                dayDate,
                isToday: DateHelpers.IsSameDay(dayDate, today),
                eventsByDate, calendars, onDaySelected, onDayActivated, onEventClicked);

            _dayColumns[DateHelpers.GetLocalDayKey(dayDate)] = cell;
            Grid.SetColumn(cell, column);
            _weekGrid.Children.Add(cell);
            column++;
        }
    }

    public void UpdateSelectedDate(DateTime previousDate, DateTime selectedDate)
    {
        previousDate = DateHelpers.GetLocalDayKey(previousDate);
        selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _selectedDate = selectedDate;

        ApplyColumnVisuals(previousDate);
        ApplyColumnVisuals(selectedDate);
    }

    private Border CreateDayColumn(
        DateTime dayDate, bool isToday,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDaySelected,
        Action<DateTime> onDayActivated,
        Action<Event, FrameworkElement> onEventClicked)
    {
        var dayKey = DateHelpers.GetLocalDayKey(dayDate);
        bool isSelected = DateHelpers.IsSameDay(dayDate, _selectedDate);

        var border = new Border();
        CalendarRenderHelper.ApplyDayContainerVisuals(border, isSelected);

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6),
            Spacing = 6
        };

        // Header: weekday abbreviation + circular date badge.
        var header = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        header.Children.Add(new TextBlock
        {
            Text = dayDate.ToString("ddd").ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(isToday ? Theme.AccentText : Theme.Text3)
        });

        var numberCircle = CalendarRenderHelper.CreateDayNumber(
            dayDate.Day.ToString(), size: 32, fontSize: 18, out var numberText);
        numberCircle.HorizontalAlignment = HorizontalAlignment.Center;
        CalendarRenderHelper.ApplyDayNumberVisuals(numberCircle, numberText, isSelected, isToday);
        _dayNumberCircles[dayKey] = numberCircle;
        _dayNumberBlocks[dayKey] = numberText;
        header.Children.Add(numberCircle);

        stackPanel.Children.Add(header);

        if (eventsByDate.TryGetValue(dayKey, out var events))
            stackPanel.Children.Add(CreateEventList(events, calendars, onEventClicked));

        // Single tap selects the day; double tap creates an event for it.
        border.Tapped += (s, e) => onDaySelected(dayDate);
        border.DoubleTapped += (s, e) => onDayActivated(dayDate);

        border.Child = stackPanel;
        return border;
    }

    private void ApplyColumnVisuals(DateTime dayDate)
    {
        var dayKey = DateHelpers.GetLocalDayKey(dayDate);
        bool isSelected = DateHelpers.IsSameDay(dayDate, _selectedDate);
        bool isToday = DateHelpers.IsSameDay(dayDate, DateHelpers.GetLocalDayKey(DateTime.Now));

        if (_dayColumns.TryGetValue(dayKey, out var column))
            CalendarRenderHelper.ApplyDayContainerVisuals(column, isSelected);

        if (_dayNumberCircles.TryGetValue(dayKey, out var circle)
            && _dayNumberBlocks.TryGetValue(dayKey, out var text))
            CalendarRenderHelper.ApplyDayNumberVisuals(circle, text, isSelected, isToday);
    }

    /// <summary>
    /// Builds a scrollable list of event-pill chips for one day column. Each
    /// chip shows the start time (or "All day") and title; chip visuals and tap
    /// handling come from <see cref="CalendarRenderHelper.CreateEventChip"/>.
    /// </summary>
    private static UIElement CreateEventList(
        List<Event> events, List<Calendar> calendars, Action<Event, FrameworkElement> onEventClicked)
    {
        var scrollViewer = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        var eventsPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        foreach (var evt in events)
        {
            var timeText = evt.IsAllDay
                ? "All day"
                : evt.StartTimeUtc.ToLocalTime().ToString("h:mm tt");

            eventsPanel.Children.Add(CalendarRenderHelper.CreateEventChip(
                evt, calendars, $"{timeText}  {evt.Title}", onEventClicked));
        }

        scrollViewer.Content = eventsPanel;
        return scrollViewer;
    }
}
