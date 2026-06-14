using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Renders the Month View: the Sun–Sat day-of-week header row and the month
/// grid of day cells.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Owns the month's <i>layout</i> — seven star-width columns, one row
///   per week (from <see cref="DateHelpers.BuildMonthGrid"/>), and each day
///   cell's number plus a scrollable list of up to five event chips with a
///   "+N more" overflow indicator.</item>
///   <item>Distinguishes in-month from out-of-month cells, highlights the
///   selected day, and exposes <see cref="UpdateSelectedDate"/> so selection
///   can move incrementally without a full re-render.</item>
///   <item>Reports day selection, day activation, and event clicks back to the
///   caller via callbacks; it owns no navigation or event state.</item>
/// </list>
///
/// Shared day-cell and chip visuals come from <see cref="CalendarRenderHelper"/>.
/// </summary>
internal sealed class CalendarGridRenderer
{
    private readonly Grid _dayNamesGrid;
    private readonly Grid _calendarGrid;
    private readonly Dictionary<DateTime, Border> _dayCells = new();
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

        var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        for (int i = 0; i < 7; i++)
        {
            var textBlock = new TextBlock
            {
                Text = dayNames[i],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 })
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
    /// <paramref name="onEventClicked"/> fires when an event chip is
    /// pressed, passing the event and the chip element to anchor a
    /// popover to (see <see cref="Views.Popovers.EventPopover"/>).
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

        int cellIndex = 0;
        foreach (var cellDate in grid.Days())
        {
            bool isInMonth = DateHelpers.IsInMonth(cellDate, displayMonth);
            var cell = CreateDayCell(
                cellDate, isInMonth,
                isSelected: DateHelpers.IsSameDay(cellDate, selectedDate),
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

        if (_dayCells.TryGetValue(previousDate, out var previousCell))
            ApplyDayCellVisuals(previousCell, previousDate);

        if (_dayCells.TryGetValue(selectedDate, out var selectedCell))
            ApplyDayCellVisuals(selectedCell, selectedDate);
    }

    private Border CreateDayCell(
        DateTime cellDate, bool isInMonth, bool isSelected,
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
        ApplyDayCellVisuals(border, cellDate);

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(8),
            Spacing = 4
        };

        if (isInMonth)
        {
            var dayDate = cellDate;

            var dayNumber = new TextBlock
            {
                Text = cellDate.Day.ToString(),
                FontSize = 16,
                FontWeight = isSelected ? FontWeights.Bold : FontWeights.SemiBold
            };
            ApplyDayNumberVisuals(dayNumber, cellDate);
            _dayNumberBlocks[DateHelpers.GetLocalDayKey(cellDate)] = dayNumber;
            stackPanel.Children.Add(dayNumber);

            if (eventsByDate.TryGetValue(dayDate, out var events))
                stackPanel.Children.Add(CreateEventList(events, calendars, onEventClicked));

            // Single tap selects the day; double tap creates an event.
            border.Tapped += (s, e) => onDaySelected(dayDate);
            border.DoubleTapped += (s, e) => onDayActivated(dayDate);
        }
        else
        {
            CalendarRenderHelper.ApplyDayContainerVisuals(
                border,
                isSelected: false,
                isInScope: false);
        }

        border.Child = stackPanel;
        return border;
    }

    private void ApplyDayCellVisuals(Border border, DateTime cellDate)
    {
        bool isInMonth = DateHelpers.IsInMonth(cellDate, _displayMonth);
        bool isSelected = isInMonth && DateHelpers.IsSameDay(cellDate, _selectedDate);

        CalendarRenderHelper.ApplyDayContainerVisuals(border, isSelected, isInMonth);

        if (_dayNumberBlocks.TryGetValue(DateHelpers.GetLocalDayKey(cellDate), out var dayNumber))
            ApplyDayNumberVisuals(dayNumber, cellDate);
    }

    private void ApplyDayNumberVisuals(TextBlock dayNumber, DateTime cellDate)
    {
        bool isSelected = DateHelpers.IsInMonth(cellDate, _displayMonth)
            && DateHelpers.IsSameDay(cellDate, _selectedDate);

        CalendarRenderHelper.ApplyDayNumberVisuals(dayNumber, isSelected);
    }

    /// <summary>
    /// Creates a scrollable list of event chips for a single day cell.
    /// Each chip is clickable and opens the event popover, anchored to the
    /// chip itself; pointer events are marked Handled to prevent the
    /// day-cell create-dialog from also firing.
    /// </summary>
    private static UIElement CreateEventList(
        List<Event> events, List<Calendar> calendars, Action<Event, FrameworkElement> onEventClicked)
    {
        var scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 100
        };

        var eventsPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2
        };

        foreach (var evt in events.Take(5))
        {
            eventsPanel.Children.Add(CalendarRenderHelper.CreateEventChip(
                evt,
                calendars,
                evt.Title,
                onEventClicked,
                new Thickness(0, 0, 0, 2)));
        }

        if (events.Count > 5)
        {
            eventsPanel.Children.Add(new TextBlock
            {
                Text = $"+{events.Count - 5} more",
                FontSize = 10,
                Foreground = new SolidColorBrush(CalendarRenderHelper.OverflowText)
            });
        }

        scrollViewer.Content = eventsPanel;
        return scrollViewer;
    }

}
