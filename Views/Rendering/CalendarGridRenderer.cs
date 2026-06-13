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
    private readonly Grid _dayNamesGrid;
    private readonly Grid _calendarGrid;

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
    /// pressed (opens the edit-event dialog).
    /// </summary>
    public void RenderCalendarGrid(
        DateTime displayMonth,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDayClicked,
        Action<Event> onEventClicked)
    {
        _calendarGrid.Children.Clear();
        _calendarGrid.ColumnDefinitions.Clear();
        _calendarGrid.RowDefinitions.Clear();

        for (int i = 0; i < 7; i++)
            _calendarGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var monthStart = DateHelpers.GetMonthStartLocal(displayMonth);
        var firstDayOfWeek = (int)monthStart.DayOfWeek;
        var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
        var weeks = (int)Math.Ceiling((firstDayOfWeek + daysInMonth) / 7.0);

        for (int i = 0; i < weeks; i++)
            _calendarGrid.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        int cellIndex = 0;
        for (int week = 0; week < weeks; week++)
        {
            for (int day = 0; day < 7; day++)
            {
                var cell = CreateDayCell(
                    cellIndex, firstDayOfWeek, daysInMonth, monthStart,
                    eventsByDate, calendars, onDayClicked, onEventClicked);
                Grid.SetRow(cell, week);
                Grid.SetColumn(cell, day);
                _calendarGrid.Children.Add(cell);
                cellIndex++;
            }
        }
    }

    private static Border CreateDayCell(
        int cellIndex, int firstDayOfWeek, int daysInMonth, DateTime monthStart,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        Action<DateTime> onDayClicked,
        Action<Event> onEventClicked)
    {
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(
                new Windows.UI.Color { A = 200, R = 220, G = 220, B = 220 }),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(2),
            Background = new SolidColorBrush(
                new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 })
        };

        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(8),
            Spacing = 4
        };

        bool isInMonth =
            cellIndex >= firstDayOfWeek && cellIndex < firstDayOfWeek + daysInMonth;

        if (isInMonth)
        {
            int dayNumber = cellIndex - firstDayOfWeek + 1;
            var dayDate = DateHelpers.GetLocalDayKey(monthStart.AddDays(dayNumber - 1));

            stackPanel.Children.Add(new TextBlock
            {
                Text = dayNumber.ToString(),
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 0, G = 0, B = 0 })
            });

            if (eventsByDate.TryGetValue(dayDate, out var events))
                stackPanel.Children.Add(CreateEventList(events, calendars, onEventClicked));

            border.PointerPressed += (s, e) => onDayClicked(dayDate);
        }
        else
        {
            border.Background = new SolidColorBrush(
                new Windows.UI.Color { A = 255, R = 245, G = 245, B = 245 });
        }

        border.Child = stackPanel;
        return border;
    }

    /// <summary>
    /// Creates a scrollable list of event chips for a single day cell.
    /// Each chip is clickable and opens the edit dialog; pointer events are
    /// marked Handled to prevent the day-cell create-dialog from also firing.
    /// </summary>
    private static UIElement CreateEventList(
        List<Event> events, List<Calendar> calendars, Action<Event> onEventClicked)
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
                onEventClicked(capturedEvt);
            };

            eventsPanel.Children.Add(chip);
        }

        if (events.Count > 5)
        {
            eventsPanel.Children.Add(new TextBlock
            {
                Text = $"+{events.Count - 5} more",
                FontSize = 10,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 200, R = 128, G = 128, B = 128 })
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
            : new Windows.UI.Color { A = 255, R = 33, G = 150, B = 243 };
    }
}
