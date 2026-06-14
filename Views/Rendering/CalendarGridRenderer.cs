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
/// Renders the Month View: the Sun–Sat day-of-week header row and the month
/// grid of day cells.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Owns the month's <i>layout</i> — seven star-width columns, one row
///   per week (from <see cref="DateHelpers.BuildMonthGrid"/>), and each day
///   cell's circular date badge plus a list of up to four filled event-pill
///   chips with a "+N more" overflow indicator.</item>
///   <item>Distinguishes in-month from out-of-month cells, highlights today
///   (accent-filled badge) and the selected day (soft tint + accent ring), and
///   exposes <see cref="UpdateSelectedDate"/> for incremental selection.</item>
///   <item>Reports day selection, day activation, and event clicks back to the
///   caller via callbacks; it owns no navigation or event state.</item>
/// </list>
///
/// Shared day-cell and chip visuals come from <see cref="CalendarRenderHelper"/>;
/// colors come from <see cref="Theme"/>.
/// </summary>
internal sealed class CalendarGridRenderer
{
    private const int EventCap = 4;

    private readonly Grid _dayNamesGrid;
    private readonly Grid _calendarGrid;
    private readonly Dictionary<DateTime, Border> _dayCells = new();
    private readonly Dictionary<DateTime, Border> _dayNumberCircles = new();
    private readonly Dictionary<DateTime, TextBlock> _dayNumberBlocks = new();

    private DateTime _displayMonth;
    private DateTime _selectedDate;

    public CalendarGridRenderer(Grid dayNamesGrid, Grid calendarGrid)
    {
        _dayNamesGrid = dayNamesGrid;
        _calendarGrid = calendarGrid;
    }

    public void RenderDayHeaders()
    {
        _dayNamesGrid.Children.Clear();

        var dayNames = new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

        for (int i = 0; i < 7; i++)
        {
            var textBlock = new TextBlock
            {
                Text = dayNames[i],
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 6),
                Foreground = new SolidColorBrush(Theme.Text3)
            };

            Grid.SetColumn(textBlock, i);
            _dayNamesGrid.Children.Add(textBlock);
        }
    }

    /// <summary>
    /// Renders the month grid for <paramref name="displayMonth"/>.
    /// <paramref name="onDaySelected"/> fires on a single tap of an in-month
    /// cell (selects the day). <paramref name="onDayActivated"/> fires on a
    /// double tap (creates an event for that day).
    /// <paramref name="onEventClicked"/> fires when an event chip is pressed,
    /// passing the event and the chip element to anchor a popover to.
    /// </summary>
    public void RenderCalendarGrid(
        DateTime displayMonth,
        DateTime selectedDate,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDaySelected,
        Action<DateTime> onDayActivated,
        Action<Event, FrameworkElement> onEventClicked)
    {
        _displayMonth = DateHelpers.GetLocalDayKey(displayMonth);
        _selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _dayCells.Clear();
        _dayNumberCircles.Clear();
        _dayNumberBlocks.Clear();

        _calendarGrid.Children.Clear();
        _calendarGrid.ColumnDefinitions.Clear();
        _calendarGrid.RowDefinitions.Clear();

        for (int i = 0; i < 7; i++)
            _calendarGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Shared grid geometry (see DateHelpers.BuildMonthGrid) keeps the
        // main grid and mini-month navigator in lock-step.
        var grid = DateHelpers.BuildMonthGrid(displayMonth);

        for (int i = 0; i < grid.Weeks; i++)
            _calendarGrid.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        int cellIndex = 0;
        foreach (var cellDate in grid.Days())
        {
            bool isInMonth = DateHelpers.IsInMonth(cellDate, displayMonth);
            var cell = CreateDayCell(
                cellDate, isInMonth,
                isToday: DateHelpers.IsSameDay(cellDate, today),
                eventsByDate, calendars, onDaySelected, onDayActivated, onEventClicked);
            var dayKey = DateHelpers.GetLocalDayKey(cellDate);
            _dayCells[dayKey] = cell;
            Grid.SetRow(cell, cellIndex / 7);
            Grid.SetColumn(cell, cellIndex % 7);
            _calendarGrid.Children.Add(cell);
            cellIndex++;
        }
    }

    public void UpdateSelectedDate(DateTime previousDate, DateTime selectedDate)
    {
        previousDate = DateHelpers.GetLocalDayKey(previousDate);
        selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _selectedDate = selectedDate;

        ApplyDayCellVisuals(previousDate);
        ApplyDayCellVisuals(selectedDate);
    }

    private Border CreateDayCell(
        DateTime cellDate, bool isInMonth, bool isToday,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDaySelected,
        Action<DateTime> onDayActivated,
        Action<Event, FrameworkElement> onEventClicked)
    {
        var dayKey = DateHelpers.GetLocalDayKey(cellDate);
        bool isSelected = isInMonth && DateHelpers.IsSameDay(cellDate, _selectedDate);

        var border = new Border();
        CalendarRenderHelper.ApplyDayContainerVisuals(border, isSelected, isInMonth);

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(8),
            Spacing = 3
        };

        var numberCircle = CalendarRenderHelper.CreateDayNumber(
            cellDate.Day.ToString(), size: 25, fontSize: 13, out var numberText);
        numberCircle.HorizontalAlignment = HorizontalAlignment.Left;
        CalendarRenderHelper.ApplyDayNumberVisuals(numberCircle, numberText, isSelected, isToday, isInMonth);
        _dayNumberCircles[dayKey] = numberCircle;
        _dayNumberBlocks[dayKey] = numberText;
        stackPanel.Children.Add(numberCircle);

        if (isInMonth)
        {
            if (eventsByDate.TryGetValue(dayKey, out var events))
                stackPanel.Children.Add(CreateEventList(events, calendars, onEventClicked));

            // Single tap selects the day; double tap creates an event.
            border.Tapped += (s, e) => onDaySelected(cellDate);
            border.DoubleTapped += (s, e) => onDayActivated(cellDate);
        }

        border.Child = stackPanel;
        return border;
    }

    private void ApplyDayCellVisuals(DateTime cellDate)
    {
        var dayKey = DateHelpers.GetLocalDayKey(cellDate);
        bool isInMonth = DateHelpers.IsInMonth(cellDate, _displayMonth);
        bool isSelected = isInMonth && DateHelpers.IsSameDay(cellDate, _selectedDate);
        bool isToday = DateHelpers.IsSameDay(cellDate, DateHelpers.GetLocalDayKey(DateTime.Now));

        if (_dayCells.TryGetValue(dayKey, out var cell))
            CalendarRenderHelper.ApplyDayContainerVisuals(cell, isSelected, isInMonth);

        if (_dayNumberCircles.TryGetValue(dayKey, out var circle)
            && _dayNumberBlocks.TryGetValue(dayKey, out var text))
            CalendarRenderHelper.ApplyDayNumberVisuals(circle, text, isSelected, isToday, isInMonth);
    }

    /// <summary>
    /// Creates the list of event-pill chips for a single day cell, capped at
    /// <see cref="EventCap"/> with a "+N more" indicator for the remainder.
    /// </summary>
    private static UIElement CreateEventList(
        List<Event> events, List<Calendar> calendars, Action<Event, FrameworkElement> onEventClicked)
    {
        var eventsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 3
        };

        int shown = Math.Min(events.Count, EventCap);
        for (int i = 0; i < shown; i++)
        {
            var evt = events[i];
            eventsPanel.Children.Add(CalendarRenderHelper.CreateEventChip(
                evt, calendars, FormatChipText(evt), onEventClicked));
        }

        if (events.Count > EventCap)
        {
            eventsPanel.Children.Add(new TextBlock
            {
                Text = $"+{events.Count - EventCap} more",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(4, 1, 0, 0),
                Foreground = new SolidColorBrush(CalendarRenderHelper.OverflowText)
            });
        }

        return eventsPanel;
    }

    private static string FormatChipText(Event evt)
    {
        if (evt.IsAllDay)
            return evt.Title;

        var start = evt.StartTimeUtc.ToLocalTime();
        var time = start.Minute == 0 ? start.ToString("h tt") : start.ToString("h:mm tt");
        return $"{time}  {evt.Title}";
    }
}
