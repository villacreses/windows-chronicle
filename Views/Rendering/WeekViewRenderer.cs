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
/// Renders the Week View into a host <see cref="Grid"/>: a row of clickable day
/// headers, an optional all-day events band, and seven 24-hour timeline columns
/// (one per day, Sun→Sat) that all scroll vertically in sync behind a single
/// shared time gutter.
///
/// Responsibilities:
/// <list type="bullet">
///   <item>Derives the seven visible days from the selected date via
///   <see cref="DateHelpers.BuildWeek"/>; no separate week or day state is
///   stored — rebuilding is the only update path.</item>
///   <item>Delegates each day's timeline to
///   <see cref="TimelineRenderHelper.BuildDayColumnContent"/>; the shared gutter
///   comes from <see cref="TimelineRenderHelper.BuildSharedGutter"/>. Each
///   day's overlap packing is independent.</item>
///   <item>Reports day-header taps, empty time-slot taps, and event taps
///   back to the host via <see cref="ICalendarInteractionHost"/>.</item>
/// </list>
///
/// Colors from <see cref="Theme"/>; shared visuals from
/// <see cref="CalendarRenderHelper"/>. The renderer is stateless beyond
/// <c>_host</c> — each <see cref="Render"/> call rebuilds from scratch.
/// </summary>
internal sealed class WeekViewRenderer
{
    private readonly Grid _host;
    private readonly ICalendarInteractionHost _interactions;

    public WeekViewRenderer(Grid host, ICalendarInteractionHost interactions)
    {
        _host = host;
        _interactions = interactions;
    }

    /// <summary>
    /// Renders the week containing <paramref name="selectedDate"/> as a
    /// 7-column 24-hour timeline grid.
    /// <paramref name="showAllDayBand"/> controls whether all-day events are
    /// shown in a band above the timelines (the band is omitted entirely when
    /// no day in the week has all-day events, regardless of this flag).
    /// Day-header taps, empty-slot taps, and event taps all route through
    /// <see cref="ICalendarInteractionHost"/>.
    /// </summary>
    public void Render(
        DateTime selectedDate,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        bool showAllDayBand)
    {
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();
        _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var weekDays = DateHelpers.BuildWeek(selectedDate);
        var today = DateHelpers.GetLocalDayKey(DateTime.Now);
        var selKey = DateHelpers.GetLocalDayKey(selectedDate);

        // Row 0: sticky top section — day headers + optional all-day band.
        var topSection = new StackPanel { Orientation = Orientation.Vertical };
        topSection.Children.Add(BuildDayHeaders(weekDays, today, selKey, _interactions));
        if (showAllDayBand)
        {
            var allDayBand = BuildAllDayBand(weekDays, eventsByDate, calendars, _interactions);
            if (allDayBand is not null)
                topSection.Children.Add(allDayBand);
        }
        Grid.SetRow(topSection, 0);
        _host.Children.Add(topSection);

        // Row 1: scrollable 7-column timeline.
        var timelinesGrid = BuildTimelinesGrid(weekDays, eventsByDate, calendars, _interactions);

        // Auto-scroll to ~7am, or earlier if the first timed event across the
        // whole week starts before then.
        var allTimed = weekDays
            .SelectMany(d => eventsByDate.GetValueOrDefault(DateHelpers.GetLocalDayKey(d)) ?? new List<Event>())
            .Where(e => !e.IsAllDay)
            .ToList();
        double targetHour = 7;
        if (allTimed.Count > 0)
            targetHour = Math.Min(targetHour, allTimed.Min(e => e.StartTimeUtc.ToLocalTime().Hour));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = timelinesGrid
        };
        var targetY = Math.Max(0, targetHour) * TimelineRenderHelper.HourHeight;
        scroll.DispatcherQueue.TryEnqueue(() =>
            scroll.ChangeView(null, targetY, null, disableAnimation: true));

        Grid.SetRow(scroll, 1);
        _host.Children.Add(scroll);
    }

    // ── Day headers ───────────────────────────────────────────────────────

    private static FrameworkElement BuildDayHeaders(
        IReadOnlyList<DateTime> weekDays,
        DateTime today,
        DateTime selKey,
        ICalendarInteractionHost interactions)
    {
        var grid = new Grid { Height = 56 };
        // Gutter placeholder keeps headers aligned with the timeline columns below.
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineRenderHelper.GutterWidth) });
        foreach (var _ in weekDays)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutterSpacer = new Border
        {
            BorderBrush = new SolidColorBrush(Theme.Hairline2),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        Grid.SetColumn(gutterSpacer, 0);
        grid.Children.Add(gutterSpacer);

        for (int i = 0; i < weekDays.Count; i++)
        {
            var dayDate = weekDays[i];
            bool isToday = DateHelpers.IsSameDay(dayDate, today);
            bool isSelected = DateHelpers.IsSameDay(dayDate, selKey);
            var captured = dayDate;

            var header = BuildDayHeader(dayDate, isToday, isSelected, () => interactions.OnDaySelected(captured));
            Grid.SetColumn(header, i + 1);
            grid.Children.Add(header);
        }
        return grid;
    }

    private static Border BuildDayHeader(DateTime dayDate, bool isToday, bool isSelected, Action onClicked)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(new TextBlock
        {
            Text = dayDate.ToString("ddd").ToUpperInvariant(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = new SolidColorBrush(isToday ? Theme.AccentText : Theme.Text3)
        });

        var circle = CalendarRenderHelper.CreateDayNumber(
            dayDate.Day.ToString(), size: 32, fontSize: 16, out var numberText);
        circle.HorizontalAlignment = HorizontalAlignment.Center;
        CalendarRenderHelper.ApplyDayNumberVisuals(circle, numberText, isSelected, isToday);
        stack.Children.Add(circle);

        var border = new Border
        {
            Child = stack,
            Padding = new Thickness(4, 8, 4, 8),
            BorderBrush = new SolidColorBrush(Theme.Hairline2),
            // Left divider separates adjacent headers; bottom rule separates headers from timelines.
            BorderThickness = new Thickness(1, 0, 0, 1)
        };
        border.Tapped += (s, e) => onClicked();
        return border;
    }

    // ── All-day band ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a 7-column all-day events band aligned with the timeline gutter
    /// and day columns below. Returns null if no day in the week has all-day
    /// events (callers skip adding it to the top section entirely).
    /// </summary>
    private static Border? BuildAllDayBand(
        IReadOnlyList<DateTime> weekDays,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        ICalendarInteractionHost interactions)
    {
        var allDayPerDay = weekDays.Select(d =>
            (eventsByDate.GetValueOrDefault(DateHelpers.GetLocalDayKey(d)) ?? new List<Event>())
            .Where(e => e.IsAllDay).ToList()).ToList();

        if (!allDayPerDay.Any(list => list.Count > 0))
            return null;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineRenderHelper.GutterWidth) });
        foreach (var _ in weekDays)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var gutterLabel = new TextBlock
        {
            Text = "all-day",
            FontSize = 10,
            Foreground = new SolidColorBrush(Theme.Text3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 8, 0)
        };
        Grid.SetColumn(gutterLabel, 0);
        grid.Children.Add(gutterLabel);

        for (int i = 0; i < allDayPerDay.Count; i++)
        {
            var events = allDayPerDay[i];
            if (events.Count == 0) continue;

            var chips = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Margin = new Thickness(1, 2, 1, 2)
            };
            foreach (var evt in events)
                chips.Children.Add(CalendarRenderHelper.CreateEventChip(evt, calendars, evt.Title, interactions));

            Grid.SetColumn(chips, i + 1);
            grid.Children.Add(chips);
        }

        return new Border
        {
            Child = grid,
            Padding = new Thickness(0, 2, 0, 4),
            BorderBrush = new SolidColorBrush(Theme.Hairline2),
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    // ── 7-column timeline grid ────────────────────────────────────────────

    private static Grid BuildTimelinesGrid(
        IReadOnlyList<DateTime> weekDays,
        Dictionary<DateTime, List<Event>> eventsByDate,
        List<Calendar> calendars,
        ICalendarInteractionHost interactions)
    {
        var grid = new Grid { Height = TimelineRenderHelper.TotalHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TimelineRenderHelper.GutterWidth) });
        for (int i = 0; i < weekDays.Count; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Single shared gutter on the far left (aligns with the header gutter spacer).
        var gutter = TimelineRenderHelper.BuildSharedGutter();
        Grid.SetColumn(gutter, 0);
        grid.Children.Add(gutter);

        // One day-content column per day; each handles its own overlap packing.
        for (int i = 0; i < weekDays.Count; i++)
        {
            var dayDate = weekDays[i];
            var dayKey = DateHelpers.GetLocalDayKey(dayDate);
            var timedEvents = (eventsByDate.GetValueOrDefault(dayKey) ?? new List<Event>())
                .Where(e => !e.IsAllDay).ToList();

            var capturedDate = dayDate;
            var dayContent = TimelineRenderHelper.BuildDayColumnContent(
                dayDate,
                timedEvents,
                calendars,
                TimeZoneInfo.Local,
                interactions,
                time => interactions.OnTimeSlotCreateRequested(capturedDate, time));

            // Border wrapper supplies the 1px left hairline divider between columns.
            var wrapper = new Border
            {
                Child = dayContent,
                BorderBrush = new SolidColorBrush(Theme.Hairline2),
                BorderThickness = new Thickness(1, 0, 0, 0)
            };
            Grid.SetColumn(wrapper, i + 1);
            grid.Children.Add(wrapper);
        }

        return grid;
    }
}
