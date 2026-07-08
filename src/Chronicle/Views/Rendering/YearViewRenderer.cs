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
/// Renders the Year View: a 4×3 grid of 12 mini-month blocks for the year
/// containing <c>_displayMonth</c>. Each block is a month label, a
/// single-letter weekday strip, and a 6×7 grid of tiny day cells tinted by
/// event density.
///
/// Density is a bucketed count of events on that local day taken directly
/// from <c>_eventsByDate</c> — no expansion, no parallel index. The bucket
/// thresholds (0 / 1–2 / 3–5 / 6+) are the same across all 12 months so the
/// heatmap reads consistently across the year.
///
/// Tapping a day cell fires <see cref="ICalendarInteractionHost.OnYearDaySelected"/>,
/// which drills from Year into Month at that day. Year cells always mean
/// "take me there" — the view is a top-down overview, not a focus surface.
///
/// The renderer is a pure consumer of the projection cache. It does not
/// query, does not maintain per-day visuals across renders (unlike the
/// bounded Month/Week/Day scaffolding rule — a full year is 372 tiny cells
/// with no partial-update surface worth optimizing for), and owns no
/// state beyond the persistent <see cref="ScrollViewer"/> — kept so the
/// user's scroll position through the year survives refreshes.
/// </summary>
internal sealed class YearViewRenderer
{
    private readonly Grid _host;
    private readonly ICalendarInteractionHost _interactions;

    private ScrollViewer? _scroll;

    public YearViewRenderer(Grid host, ICalendarInteractionHost interactions)
    {
        _host = host;
        _interactions = interactions;
    }

    /// <summary>
    /// Renders the 12 months of <paramref name="displayYear"/>'s calendar year,
    /// using <paramref name="eventsByDate"/> for density tinting and
    /// <paramref name="selectedDate"/> / today for highlight state.
    /// </summary>
    public void Render(
        int displayYear,
        DateTime selectedDate,
        Dictionary<DateTime, List<Event>> eventsByDate)
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

        var grid = new Grid { Padding = new Thickness(4, 4, 12, 12) };
        for (int c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var today = DateHelpers.GetLocalDayKey(DateTime.Now);
        var selected = DateHelpers.GetLocalDayKey(selectedDate);

        for (int m = 0; m < 12; m++)
        {
            var monthStart = new DateTime(displayYear, m + 1, 1, 0, 0, 0, DateTimeKind.Local);
            var block = BuildMonthBlock(monthStart, today, selected, eventsByDate, _interactions);
            Grid.SetColumn(block, m % 4);
            Grid.SetRow(block, m / 4);
            grid.Children.Add(block);
        }

        _scroll.Content = grid;
    }

    private static StackPanel BuildMonthBlock(
        DateTime monthStart,
        DateTime today,
        DateTime selected,
        Dictionary<DateTime, List<Event>> eventsByDate,
        ICalendarInteractionHost interactions)
    {
        var block = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(4, 6, 4, 6),
            Spacing = 2
        };

        block.Children.Add(new TextBlock
        {
            Text = monthStart.ToString("MMMM"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Theme.Text),
            Margin = new Thickness(2, 0, 0, 2)
        });

        block.Children.Add(BuildWeekdayRow());
        block.Children.Add(BuildDayGrid(monthStart, today, selected, eventsByDate, interactions));

        return block;
    }

    private static Grid BuildWeekdayRow()
    {
        var row = new Grid();
        for (int i = 0; i < 7; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var letters = new[] { "S", "M", "T", "W", "T", "F", "S" };
        for (int i = 0; i < 7; i++)
        {
            var letter = new TextBlock
            {
                Text = letters[i],
                FontSize = 9,
                Foreground = new SolidColorBrush(Theme.Text3),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetColumn(letter, i);
            row.Children.Add(letter);
        }

        return row;
    }

    private static Grid BuildDayGrid(
        DateTime monthStart,
        DateTime today,
        DateTime selected,
        Dictionary<DateTime, List<Event>> eventsByDate,
        ICalendarInteractionHost interactions)
    {
        var grid = new Grid();
        for (int i = 0; i < 7; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var monthGrid = DateHelpers.BuildMonthGrid(monthStart);
        for (int i = 0; i < monthGrid.Weeks; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int cellIndex = 0;
        foreach (var cellDate in monthGrid.Days())
        {
            var isInMonth = DateHelpers.IsInMonth(cellDate, monthStart);
            var count = isInMonth
                ? (eventsByDate.TryGetValue(cellDate, out var list) ? list.Count : 0)
                : 0;

            var cell = BuildDayCell(
                cellDate,
                isInMonth: isInMonth,
                isToday: DateHelpers.IsSameDay(cellDate, today),
                isSelected: DateHelpers.IsSameDay(cellDate, selected),
                densityCount: count,
                interactions: interactions);

            Grid.SetRow(cell, cellIndex / 7);
            Grid.SetColumn(cell, cellIndex % 7);
            grid.Children.Add(cell);
            cellIndex++;
        }

        return grid;
    }

    private static Button BuildDayCell(
        DateTime cellDate,
        bool isInMonth,
        bool isToday,
        bool isSelected,
        int densityCount,
        ICalendarInteractionHost interactions)
    {
        var button = new Button
        {
            Content = cellDate.Day.ToString(),
            FontSize = 10,
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        // Visual precedence (dense → sparse):
        //   selected > today > density tint > plain in/out of month.
        // Density paints only when nothing higher-priority claims the cell,
        // keeping the year read as "these days have events" rather than
        // fighting the selection / today markers.
        if (isSelected && isInMonth)
        {
            button.Background = new SolidColorBrush(Theme.Accent);
            button.Foreground = new SolidColorBrush(Theme.OnAccent);
            button.FontWeight = FontWeights.SemiBold;
        }
        else if (isToday && isInMonth)
        {
            button.Foreground = new SolidColorBrush(Theme.AccentText);
            button.FontWeight = FontWeights.SemiBold;
            button.BorderBrush = new SolidColorBrush(Theme.Accent);
            button.BorderThickness = new Thickness(1);
        }
        else if (isInMonth)
        {
            var tint = DensityTint(densityCount);
            if (tint is Windows.UI.Color color)
                button.Background = new SolidColorBrush(color);

            button.Foreground = new SolidColorBrush(Theme.Text2);
        }
        else
        {
            // Out-of-month cells always render un-tinted regardless of density
            // — density belongs to the month that owns the day. Keeping them
            // dim and readable avoids the year grid stitching visually.
            button.Foreground = new SolidColorBrush(Theme.Text4);
        }

        if (isInMonth)
            button.Click += (s, e) => interactions.OnYearDaySelected(cellDate);
        else
            button.IsEnabled = false;

        return button;
    }

    /// <summary>
    /// Buckets a day's event count into the four-tier density ramp used by
    /// the Year View heatmap: 0 → no tint, 1–2 → light, 3–5 → medium, 6+ →
    /// heavy. Thresholds are fixed rather than data-relative so the same
    /// count reads the same in a sparse January as a busy October.
    /// </summary>
    private static Windows.UI.Color? DensityTint(int count) => count switch
    {
        0 => null,
        <= 2 => Theme.AccentDensity1,
        <= 5 => Theme.AccentDensity2,
        _ => Theme.AccentDensity3
    };
}
