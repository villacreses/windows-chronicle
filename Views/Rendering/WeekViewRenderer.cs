using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Dispatching;
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
///   stored.</item>
///   <item>Delegates each day's timeline to
///   <see cref="TimelineRenderHelper.BuildDayColumnContent"/>; the shared gutter
///   comes from <see cref="TimelineRenderHelper.BuildSharedGutter"/>. Each
///   day's overlap packing is independent.</item>
///   <item>Reports day-header taps, empty time-slot taps, and event taps
///   back to the host via <see cref="ICalendarInteractionHost"/>.</item>
///   <item>Retains per-day day-number visuals so <see cref="UpdateSelectedDate"/>
///   can mutate the previous and new selected-day highlights in place without
///   rebuilding columns, gridlines, chips, or scroll state. Full
///   <see cref="Render"/> is reserved for range changes (cross-week navigation).</item>
///   <item>Holds the timeline <see cref="ScrollViewer"/> as a persistent
///   field — created on first <see cref="Render"/>, reused thereafter. Only
///   its <see cref="ScrollViewer.Content"/> is swapped on subsequent renders,
///   so <see cref="ScrollViewer.VerticalOffset"/> survives draft-chip
///   show/hide, save, cancel, and visibility-toggle refreshes. The renderer
///   never moves the scroll position; the user's offset is preserved as-is.</item>
/// </list>
///
/// Colors from <see cref="Theme"/>; shared visuals from
/// <see cref="CalendarRenderHelper"/>.
/// </summary>
internal sealed class WeekViewRenderer
{
    private readonly Grid _host;
    private readonly ICalendarInteractionHost _interactions;
    // Per-day-header visuals retained from the last Render() so selection-only
    // changes can update highlights in place. Selection within the visible
    // week must not reallocate columns, gridlines, chips, or scroll state —
    // see "Bounded Visuals Are Reused" in .context/architecture/USER_INTERFACE.md.
    private readonly Dictionary<DateTime, Border> _dayNumberCircles = new();
    private readonly Dictionary<DateTime, TextBlock> _dayNumberBlocks = new();

    // Persistent visuals created on first Render() and reused thereafter.
    // Keeping the ScrollViewer instance is what preserves the user's scroll
    // offset across re-renders — a fresh ScrollViewer would start at 0.
    private ScrollViewer? _scroll;
    private FrameworkElement? _topSection;

    private DateTime _selectedDate;

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
        _selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _dayNumberCircles.Clear();
        _dayNumberBlocks.Clear();

        var weekDays = DateHelpers.BuildWeek(selectedDate);
        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        // First-time init: lay out the host (Auto row + Star row) and add the
        // persistent ScrollViewer. After this, _scroll stays in _host.Children
        // for the lifetime of the renderer so its VerticalOffset survives
        // every subsequent Render().
        if (_scroll is null)
        {
            _host.Children.Clear();
            _host.ColumnDefinitions.Clear();
            _host.RowDefinitions.Clear();
            _host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            _scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(_scroll, 1);
            _host.Children.Add(_scroll);

            // One-shot starting offset: seed the scroll to ~7am on first mount
            // so the user lands at a sensible time of day instead of midnight.
            // Deferred to Low priority so the first Content swap below has been
            // measured/arranged before ChangeView runs (calling it before layout
            // silently no-ops). After this, the renderer never moves the scroll
            // position again — subsequent Render() calls only swap Content and
            // VerticalOffset is preserved.
            var scroll = _scroll;
            scroll.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                scroll.ChangeView(null, 7 * TimelineRenderHelper.HourHeight, null, disableAnimation: true));
        }

        // Row 0: replace the top section — day headers + optional all-day band.
        // Always rebuilt because the headers carry per-render selection /
        // today highlights and the all-day band's contents depend on events.
        // The ScrollViewer below is untouched, so its scroll position holds.
        if (_topSection is not null)
            _host.Children.Remove(_topSection);

        var topSection = new StackPanel { Orientation = Orientation.Vertical };
        topSection.Children.Add(BuildDayHeaders(weekDays, today, _selectedDate));
        if (showAllDayBand)
        {
            var allDayBand = BuildAllDayBand(weekDays, eventsByDate, calendars, _interactions);
            if (allDayBand is not null)
                topSection.Children.Add(allDayBand);
        }
        Grid.SetRow(topSection, 0);
        _host.Children.Add(topSection);
        _topSection = topSection;

        // Swap the timelines into the persistent ScrollViewer. Setting Content
        // doesn't reset VerticalOffset; the user stays where they were
        // looking. No programmatic scroll ever.
        _scroll.Content = BuildTimelinesGrid(weekDays, eventsByDate, calendars, _interactions);
    }

    /// <summary>
    /// Selection-only update for a day already in the currently rendered week.
    /// Mutates the previous and new selected-day highlight in place — chips,
    /// gridlines, timeline columns, gutter, and scroll state are not touched.
    /// Cross-week selection must go through a full <see cref="Render"/>
    /// instead (the visible seven days change).
    /// </summary>
    public void UpdateSelectedDate(DateTime previousDate, DateTime selectedDate)
    {
        previousDate = DateHelpers.GetLocalDayKey(previousDate);
        selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _selectedDate = selectedDate;

        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        ApplyDayHeaderVisuals(previousDate, today);
        ApplyDayHeaderVisuals(selectedDate, today);
    }

    private void ApplyDayHeaderVisuals(DateTime dayKey, DateTime today)
    {
        if (!_dayNumberCircles.TryGetValue(dayKey, out var circle)
            || !_dayNumberBlocks.TryGetValue(dayKey, out var numberText))
            return;

        bool isSelected = DateHelpers.IsSameDay(dayKey, _selectedDate);
        bool isToday = DateHelpers.IsSameDay(dayKey, today);
        CalendarRenderHelper.ApplyDayNumberVisuals(circle, numberText, isSelected, isToday);
    }

    // ── Day headers ───────────────────────────────────────────────────────

    private FrameworkElement BuildDayHeaders(
        IReadOnlyList<DateTime> weekDays,
        DateTime today,
        DateTime selKey)
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

            var header = BuildDayHeader(dayDate, isToday, isSelected, () => _interactions.OnDaySelected(captured));
            Grid.SetColumn(header, i + 1);
            grid.Children.Add(header);
        }
        return grid;
    }

    private Border BuildDayHeader(DateTime dayDate, bool isToday, bool isSelected, Action onClicked)
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

        var dayKey = DateHelpers.GetLocalDayKey(dayDate);
        _dayNumberCircles[dayKey] = circle;
        _dayNumberBlocks[dayKey] = numberText;

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
