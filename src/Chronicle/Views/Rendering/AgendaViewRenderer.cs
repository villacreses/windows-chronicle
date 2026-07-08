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
/// Renders the Agenda View: a chronological list of upcoming events from
/// <paramref name="rangeStartLocal"/> through the last day of the following
/// month. Days with no events are omitted — that is what makes this an
/// agenda, not a Day-view repeated 55 times. Days with events show a small
/// header row (weekday, date) followed by event rows in the order fixed by
/// <see cref="Projection.EventProjection.OrderForDay"/> (all-day first,
/// then by start time).
///
/// The renderer is a pure consumer of the projection cache
/// (<c>_eventsByDate</c>) plus a date range. It does not query, does not
/// page (Prev/Next are disabled while Agenda is active), and owns no state
/// beyond the persistent <see cref="ScrollViewer"/>.
/// </summary>
internal sealed class AgendaViewRenderer
{
    private readonly Grid _host;
    private readonly ICalendarInteractionHost _interactions;

    // Persistent scroll container: created on first Render(), reused
    // thereafter. Only its Content is swapped on subsequent renders.
    //
    // Load-bearing UX invariant, not an optimization. In Month / Week / Day,
    // the user's context is _selectedDate — re-renders that reset scroll to
    // the top still land them where they were. Agenda has no selected date:
    // its range is anchored to today, and the user's read position is
    // encoded entirely in ScrollViewer.VerticalOffset. Recreating the
    // ScrollViewer on every Render() (event CRUD, calendar visibility
    // toggle, calendar-list mutation) would silently teleport the user
    // back to today whenever anything changed. See USER_INTERFACE.md
    // "Scroll offset is view state."
    private ScrollViewer? _scroll;

    public AgendaViewRenderer(Grid host, ICalendarInteractionHost interactions)
    {
        _host = host;
        _interactions = interactions;
    }

    /// <summary>
    /// Renders the events grouped in <paramref name="eventsByDate"/> between
    /// <paramref name="rangeStartLocal"/> (inclusive) and
    /// <paramref name="rangeEndLocal"/> (inclusive). The range bounds are
    /// local day keys — the caller usually derives them via
    /// <see cref="DateHelpers.GetAgendaRangeUtc"/> and converts back.
    /// </summary>
    public void Render(
        DateTime rangeStartLocal,
        DateTime rangeEndLocal,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars)
    {
        if (_scroll is null)
        {
            _host.Children.Clear();
            _host.ColumnDefinitions.Clear();
            _host.RowDefinitions.Clear();

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            _host.Children.Add(_scroll);
        }

        var startKey = DateHelpers.GetLocalDayKey(rangeStartLocal);
        var endKey = DateHelpers.GetLocalDayKey(rangeEndLocal);
        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        var content = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 12,
            Padding = new Thickness(4, 4, 12, 12)
        };

        bool anyDay = false;
        for (var day = startKey; day <= endKey; day = day.AddDays(1))
        {
            if (!eventsByDate.TryGetValue(day, out var dayEvents) || dayEvents.Count == 0)
                continue;

            anyDay = true;
            content.Children.Add(BuildDaySection(day, today, dayEvents, calendars, _interactions));
        }

        if (!anyDay)
        {
            content.Children.Add(new TextBlock
            {
                Text = "No upcoming events.",
                FontSize = 13,
                Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText),
                Margin = new Thickness(4, 8, 0, 0)
            });
        }

        _scroll.Content = content;
    }

    private static StackPanel BuildDaySection(
        DateTime day,
        DateTime today,
        List<Event> dayEvents,
        List<Calendar> calendars,
        ICalendarInteractionHost interactions)
    {
        var section = new StackPanel { Orientation = Orientation.Vertical, Spacing = 4 };

        section.Children.Add(BuildDayHeader(day, today));

        foreach (var evt in dayEvents)
            section.Children.Add(BuildEventRow(evt, calendars, interactions));

        return section;
    }

    private static FrameworkElement BuildDayHeader(DateTime day, DateTime today)
    {
        var isToday = day == today;
        var text = isToday
            ? $"Today · {day:ddd, MMM d}"
            : day.ToString("ddd, MMM d");

        return new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Medium,
            Foreground = new SolidColorBrush(isToday ? Theme.Accent : Theme.Text2),
            Margin = new Thickness(2, 0, 0, 2)
        };
    }

    private static Button BuildEventRow(
        Event evt, List<Calendar> calendars, ICalendarInteractionHost interactions)
    {
        var capturedEvt = evt;

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

        if (!string.IsNullOrWhiteSpace(capturedEvt.Description))
        {
            textColumn.Children.Add(new TextBlock
            {
                Text = capturedEvt.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            });
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
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
        row.Click += (s, e) => interactions.OnEventActivated(capturedEvt, (FrameworkElement)s);

        return row;
    }
}
