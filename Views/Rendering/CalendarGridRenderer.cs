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
/// Builds the day-of-week header row and the month grid of day cells,
/// including the event chips rendered inside each cell.
/// </summary>
internal sealed class CalendarGridRenderer
{
    private static readonly Windows.UI.Color InMonthBackground =
        new() { A = 255, R = 255, G = 255, B = 255 };

    private static readonly Windows.UI.Color OutOfMonthBackground =
        new() { A = 255, R = 245, G = 245, B = 245 };

    private static readonly Windows.UI.Color CellBorder =
        new() { A = 200, R = 220, G = 220, B = 220 };

    private static readonly Windows.UI.Color SelectedBackground =
        new() { A = 255, R = 239, G = 246, B = 255 };

    private static readonly Windows.UI.Color DayNumberText =
        new() { A = 255, R = 0, G = 0, B = 0 };

    private static readonly Windows.UI.Color MutedText =
        new() { A = 200, R = 128, G = 128, B = 128 };

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
    /// <paramref name="onDayClicked"/> fires when an in-month cell's
    /// background is pressed (opens the create-event dialog).
    /// <paramref name="onEventClicked"/> fires when an event chip is
    /// pressed, passing the event and the chip element to anchor a
    /// popover to (see <see cref="Views.Popovers.EventPopover"/>).
    /// </summary>
    public void RenderCalendarGrid(
        DateTime displayMonth,
        DateTime selectedDate,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDayClicked,
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
                eventsByDate, calendars, onDayClicked, onEventClicked);
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
        Action<DateTime> onDayClicked,
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

            border.PointerPressed += (s, e) => onDayClicked(dayDate);
        }
        else
        {
            border.Background = new SolidColorBrush(OutOfMonthBackground);
        }

        border.Child = stackPanel;
        return border;
    }

    private void ApplyDayCellVisuals(Border border, DateTime cellDate)
    {
        bool isInMonth = DateHelpers.IsInMonth(cellDate, _displayMonth);
        bool isSelected = isInMonth && DateHelpers.IsSameDay(cellDate, _selectedDate);

        border.Background = new SolidColorBrush(
            isSelected ? SelectedBackground : isInMonth ? InMonthBackground : OutOfMonthBackground);
        border.BorderBrush = new SolidColorBrush(isSelected ? ColorHelper.AppAccent : CellBorder);
        border.BorderThickness = new Thickness(isSelected ? 2 : 1);

        if (_dayNumberBlocks.TryGetValue(DateHelpers.GetLocalDayKey(cellDate), out var dayNumber))
            ApplyDayNumberVisuals(dayNumber, cellDate);
    }

    private void ApplyDayNumberVisuals(TextBlock dayNumber, DateTime cellDate)
    {
        bool isSelected = DateHelpers.IsInMonth(cellDate, _displayMonth)
            && DateHelpers.IsSameDay(cellDate, _selectedDate);

        dayNumber.Foreground = new SolidColorBrush(isSelected ? ColorHelper.AppAccent : DayNumberText);
        dayNumber.FontWeight = isSelected ? FontWeights.Bold : FontWeights.SemiBold;
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
            var capturedEvt = evt;

            var calColor = ResolveCalendarColor(calendars, capturedEvt.CalendarId);

            var eventTextBlock = new TextBlock
            {
                Text = capturedEvt.Title,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(calColor),
                Margin = new Thickness(0, 0, 0, 2)
            };

            var chip = new Border
            {
                Child = eventTextBlock,
                Background = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 })
            };

            chip.PointerPressed += (s, e) =>
            {
                e.Handled = true;
                onEventClicked(capturedEvt, chip);
            };

            eventsPanel.Children.Add(chip);
        }

        if (events.Count > 5)
        {
            eventsPanel.Children.Add(new TextBlock
            {
                Text = $"+{events.Count - 5} more",
                FontSize = 10,
                Foreground = new SolidColorBrush(MutedText)
            });
        }

        scrollViewer.Content = eventsPanel;
        return scrollViewer;
    }

    /// <summary>
    /// Returns the <see cref="Windows.UI.Color"/> for the given calendar.
    /// Falls back to blue if the calendar is not found.
    /// </summary>
    private static Windows.UI.Color ResolveCalendarColor(List<Calendar> calendars, Guid calendarId)
    {
        var cal = calendars.FirstOrDefault(c => c.Id == calendarId);
        return cal is not null
            ? ColorHelper.ParseHexColor(cal.Color)
            : ColorHelper.AppAccent;
    }
}
