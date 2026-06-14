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
/// Renders the Selected Day section of the sidebar.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Shows the selected date, an event count, and a clickable list of
///   that day's events, or a "No events scheduled." empty state.</item>
///   <item>Builds event rows as full-width <see cref="Button"/>s whose click
///   opens the edit dialog directly — a deliberately different interaction
///   from the grid/week event chips, which open the read-only popover.</item>
///   <item>Reads the pre-filtered, pre-sorted events handed to
///   <see cref="Render"/>; it owns no navigation or event state and performs
///   no querying. Kept separate from <see cref="SidebarRenderer"/> so
///   calendar-list and selected-day concerns don't share a class.</item>
/// </list>
/// </summary>
internal sealed class SelectedDayRenderer
{
    private readonly StackPanel _container;

    public SelectedDayRenderer(StackPanel container)
    {
        _container = container;
    }

    /// <summary>
    /// Renders the panel for <paramref name="selectedDate"/> and its
    /// <paramref name="events"/> (already filtered to visible calendars and
    /// ordered by start time). <paramref name="onEventClicked"/> fires when
    /// an event row is clicked (opens the edit dialog).
    /// </summary>
    public void Render(
        DateTime selectedDate,
        List<Event> events,
        List<Calendar> calendars,
        Action<Event> onEventClicked)
    {
        _container.Children.Clear();

        // Date header
        _container.Children.Add(new TextBlock
        {
            Text = selectedDate.ToString("dddd, MMM d"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold
        });

        // Event count
        _container.Children.Add(new TextBlock
        {
            Text = $"{events.Count} event{(events.Count == 1 ? "" : "s")}",
            FontSize = 12,
            Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText)
        });

        if (events.Count == 0)
        {
            _container.Children.Add(new TextBlock
            {
                Text = "No events scheduled.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText),
                Margin = new Thickness(0, 2, 0, 0)
            });
            return;
        }

        foreach (var evt in events)
            _container.Children.Add(BuildEventRow(evt, calendars, onEventClicked));
    }

    private static Button BuildEventRow(
        Event evt, List<Calendar> calendars, Action<Event> onEventClicked)
    {
        var capturedEvt = evt;

        // Agenda-style left color bar (matches the design).
        var colorBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(
                ColorHelper.ResolveCalendarColor(calendars, capturedEvt.CalendarId)),
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 30
        };

        var timeBlock = new TextBlock
        {
            Text = capturedEvt.IsAllDay
                ? "All day"
                : capturedEvt.StartTimeUtc.ToLocalTime().ToString("h:mm tt"),
            FontSize = 11,
            Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText)
        };

        var titleBlock = new TextBlock
        {
            Text = capturedEvt.Title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var textColumn = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        textColumn.Children.Add(timeBlock);
        textColumn.Children.Add(titleBlock);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9
        };
        content.Children.Add(colorBar);
        content.Children.Add(textColumn);

        var row = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(4, 6, 4, 6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        row.Click += (s, e) => onEventClicked(capturedEvt);

        return row;
    }

}
