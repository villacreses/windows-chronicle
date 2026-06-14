using Chronicle.Helpers;
using Chronicle.Models;
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
///   column's header (weekday abbreviation + date number) and scrollable list
///   of that day's event chips (all events, no cap).</item>
///   <item>Highlights today (weekday label) and the selected day (column +
///   number), and exposes <see cref="UpdateSelectedDate"/> so selection can be
///   moved incrementally without a full re-render.</item>
///   <item>Reports day selection, day activation, and event clicks back to the
///   caller via callbacks; it owns no navigation or event state.</item>
/// </list>
///
/// Week geometry comes from <see cref="DateHelpers.BuildWeek"/> and events come
/// from the same <c>_eventsByDate</c> store the month grid uses, so this is
/// purely another consumer of the existing date/event model. Shared day-cell
/// and chip visuals come from <see cref="CalendarRenderHelper"/>.
/// </summary>
internal sealed class WeekViewRenderer
{
    private readonly Grid _weekGrid;
    private readonly Dictionary<DateTime, Border> _dayColumns = new();
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

        if (_dayColumns.TryGetValue(previousDate, out var previousCell))
            ApplyColumnVisuals(previousCell, previousDate);

        if (_dayColumns.TryGetValue(selectedDate, out var selectedCell))
            ApplyColumnVisuals(selectedCell, selectedDate);
    }

    private Border CreateDayColumn(
        DateTime dayDate, bool isToday,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDaySelected,
        Action<DateTime> onDayActivated,
        Action<Event, FrameworkElement> onEventClicked)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2)
        };
        ApplyColumnVisuals(border, dayDate);

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(6),
            Spacing = 6
        };

        // Header: weekday abbreviation + date number.
        var header = new StackPanel { Orientation = Orientation.Vertical, Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = dayDate.ToString("ddd"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(
                isToday ? ColorHelper.AppAccent : CalendarRenderHelper.MutedText)
        });

        var dayNumber = new TextBlock
        {
            Text = dayDate.Day.ToString(),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _dayNumberBlocks[DateHelpers.GetLocalDayKey(dayDate)] = dayNumber;
        CalendarRenderHelper.ApplyDayNumberVisuals(
            dayNumber, DateHelpers.IsSameDay(dayDate, _selectedDate));
        header.Children.Add(dayNumber);

        stackPanel.Children.Add(header);

        if (eventsByDate.TryGetValue(dayDate, out var events))
            stackPanel.Children.Add(CreateEventList(events, calendars, onEventClicked));

        // Single tap selects the day; double tap creates an event for it.
        border.Tapped += (s, e) => onDaySelected(dayDate);
        border.DoubleTapped += (s, e) => onDayActivated(dayDate);

        border.Child = stackPanel;
        return border;
    }

    private void ApplyColumnVisuals(Border border, DateTime dayDate)
    {
        bool isSelected = DateHelpers.IsSameDay(dayDate, _selectedDate);

        // Every day in the week is in scope, so isInScope stays true.
        CalendarRenderHelper.ApplyDayContainerVisuals(border, isSelected);

        if (_dayNumberBlocks.TryGetValue(DateHelpers.GetLocalDayKey(dayDate), out var dayNumber))
            CalendarRenderHelper.ApplyDayNumberVisuals(dayNumber, isSelected);
    }

    /// <summary>
    /// Builds a scrollable list of event chips for one day column. Each chip
    /// shows the start time (or "All day") and title; chip visuals and tap
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
                evt,
                calendars,
                $"{timeText}  {evt.Title}",
                onEventClicked));
        }

        scrollViewer.Content = eventsPanel;
        return scrollViewer;
    }
}
