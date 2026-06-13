using Chronicle.Helpers;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using Windows.System;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Builds the compact month navigator shown in the sidebar: a header with
/// prev/next arrows and the month label, a single-letter day-of-week row,
/// and a traditional month grid of clickable day cells.
///
/// Grid geometry comes from <see cref="DateHelpers.BuildMonthGrid"/>, the
/// same source the main calendar grid uses, so the two never drift apart.
/// </summary>
internal sealed class MiniMonthRenderer
{
    // Segoe MDL2 Assets glyphs (escaped to keep the source ASCII-clean).
    private const string ChevronLeftGlyph = "";
    private const string ChevronRightGlyph = "";

    private static readonly Windows.UI.Color InMonthText =
        new() { A = 255, R = 32, G = 32, B = 32 };

    private static readonly Windows.UI.Color OutOfMonthText =
        new() { A = 255, R = 180, G = 180, B = 180 };

    private static readonly Windows.UI.Color SelectedText =
        new() { A = 255, R = 255, G = 255, B = 255 };

    private readonly StackPanel _container;
    private readonly Dictionary<DateTime, Button> _dayButtons = new();

    private DateTime _displayMonth;
    private DateTime _selectedDate;
    private DateTime? _pendingFocusDate;
    private Action<DateTime>? _onDateSelected;

    public MiniMonthRenderer(StackPanel container)
    {
        _container = container;
    }

    /// <summary>
    /// Renders the mini month for <paramref name="displayMonth"/>, highlighting
    /// today and <paramref name="selectedDate"/>.
    /// <paramref name="onDateSelected"/> fires with the clicked day's date.
    /// <paramref name="onPrevMonth"/>/<paramref name="onNextMonth"/> fire when
    /// the header arrows are pressed.
    /// </summary>
    public void Render(
        DateTime displayMonth,
        DateTime selectedDate,
        Action<DateTime> onDateSelected,
        Action onPrevMonth,
        Action onNextMonth)
    {
        _displayMonth = DateHelpers.GetLocalDayKey(displayMonth);
        _selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _onDateSelected = onDateSelected;
        _dayButtons.Clear();

        _container.Children.Clear();
        _container.Children.Add(BuildHeader(displayMonth, onPrevMonth, onNextMonth));
        _container.Children.Add(BuildDayOfWeekRow());
        _container.Children.Add(BuildDayGrid(displayMonth, selectedDate, onDateSelected));
        FocusPendingDay();
    }

    public void UpdateSelectedDate(DateTime previousDate, DateTime selectedDate)
    {
        previousDate = DateHelpers.GetLocalDayKey(previousDate);
        selectedDate = DateHelpers.GetLocalDayKey(selectedDate);
        _selectedDate = selectedDate;

        if (_dayButtons.TryGetValue(previousDate, out var previousButton))
            ApplyDayCellVisuals(previousButton, previousDate);

        if (_dayButtons.TryGetValue(selectedDate, out var selectedButton))
            ApplyDayCellVisuals(selectedButton, selectedDate);

        FocusPendingDay();
    }

    private static Grid BuildHeader(DateTime displayMonth, Action onPrevMonth, Action onNextMonth)
    {
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prev = BuildArrowButton(ChevronLeftGlyph, onPrevMonth);
        Grid.SetColumn(prev, 0);
        header.Children.Add(prev);

        var label = new TextBlock
        {
            Text = displayMonth.ToString("MMMM yyyy"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 1);
        header.Children.Add(label);

        var next = BuildArrowButton(ChevronRightGlyph, onNextMonth);
        Grid.SetColumn(next, 2);
        header.Children.Add(next);

        return header;
    }

    private static Button BuildArrowButton(string glyph, Action onClick)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12
            },
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        button.Click += (s, e) => onClick();
        return button;
    }

    private static Grid BuildDayOfWeekRow()
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        for (int i = 0; i < 7; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var letters = new[] { "S", "M", "T", "W", "T", "F", "S" };
        for (int i = 0; i < 7; i++)
        {
            var letter = new TextBlock
            {
                Text = letters[i],
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = new SolidColorBrush(OutOfMonthText)
            };
            Grid.SetColumn(letter, i);
            row.Children.Add(letter);
        }

        return row;
    }

    private Grid BuildDayGrid(
        DateTime displayMonth, DateTime selectedDate, Action<DateTime> onDateSelected)
    {
        var grid = new Grid();
        for (int i = 0; i < 7; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var monthGrid = DateHelpers.BuildMonthGrid(displayMonth);
        for (int i = 0; i < monthGrid.Weeks; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var today = DateHelpers.GetLocalDayKey(DateTime.Now);

        int cellIndex = 0;
        foreach (var cellDate in monthGrid.Days())
        {
            var cell = BuildDayCell(
                cellDate,
                isInMonth: DateHelpers.IsInMonth(cellDate, displayMonth),
                isToday: DateHelpers.IsSameDay(cellDate, today),
                isSelected: DateHelpers.IsSameDay(cellDate, selectedDate),
                onDateSelected);
            _dayButtons[DateHelpers.GetLocalDayKey(cellDate)] = cell;
            Grid.SetRow(cell, cellIndex / 7);
            Grid.SetColumn(cell, cellIndex % 7);
            grid.Children.Add(cell);
            cellIndex++;
        }

        return grid;
    }

    private Button BuildDayCell(
        DateTime cellDate, bool isInMonth, bool isToday, bool isSelected,
        Action<DateTime> onDateSelected)
    {
        var button = new Button
        {
            Content = cellDate.Day.ToString(),
            FontSize = 12,
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            MinWidth = 0,
            CornerRadius = new CornerRadius(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Tag = DateHelpers.GetLocalDayKey(cellDate)
        };

        // Visual precedence: selected (filled) > today (accent text + ring) > normal.
        if (isSelected)
        {
            button.Background = new SolidColorBrush(ColorHelper.AppAccent);
            button.Foreground = new SolidColorBrush(SelectedText);
            button.FontWeight = FontWeights.SemiBold;
        }
        else if (isToday)
        {
            button.Foreground = new SolidColorBrush(ColorHelper.AppAccent);
            button.FontWeight = FontWeights.SemiBold;
            button.BorderBrush = new SolidColorBrush(ColorHelper.AppAccent);
            button.BorderThickness = new Thickness(1);
        }
        else
        {
            button.Foreground = new SolidColorBrush(isInMonth ? InMonthText : OutOfMonthText);
        }

        button.Click += (s, e) => onDateSelected(cellDate);
        button.KeyDown += DayCell_KeyDown;
        return button;
    }

    private void DayCell_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not DateTime focusedDate)
            return;

        DateTime? targetDate = e.Key switch
        {
            VirtualKey.Left => focusedDate.AddDays(-1),
            VirtualKey.Right => focusedDate.AddDays(1),
            VirtualKey.Up => focusedDate.AddDays(-7),
            VirtualKey.Down => focusedDate.AddDays(7),
            VirtualKey.Enter or VirtualKey.Space => focusedDate,
            _ => null
        };

        if (targetDate is null || _onDateSelected is null)
            return;

        e.Handled = true;
        _pendingFocusDate = DateHelpers.GetLocalDayKey(targetDate.Value);
        _onDateSelected(targetDate.Value);
    }

    private void ApplyDayCellVisuals(Button button, DateTime cellDate)
    {
        bool isInMonth = DateHelpers.IsInMonth(cellDate, _displayMonth);
        bool isToday = DateHelpers.IsSameDay(cellDate, DateHelpers.GetLocalDayKey(DateTime.Now));
        bool isSelected = DateHelpers.IsSameDay(cellDate, _selectedDate);

        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderBrush = null;
        button.BorderThickness = new Thickness(0);
        button.FontWeight = FontWeights.Normal;

        if (isSelected)
        {
            button.Background = new SolidColorBrush(ColorHelper.AppAccent);
            button.Foreground = new SolidColorBrush(SelectedText);
            button.FontWeight = FontWeights.SemiBold;
        }
        else if (isToday)
        {
            button.Foreground = new SolidColorBrush(ColorHelper.AppAccent);
            button.FontWeight = FontWeights.SemiBold;
            button.BorderBrush = new SolidColorBrush(ColorHelper.AppAccent);
            button.BorderThickness = new Thickness(1);
        }
        else
        {
            button.Foreground = new SolidColorBrush(isInMonth ? InMonthText : OutOfMonthText);
        }
    }

    private void FocusPendingDay()
    {
        if (_pendingFocusDate is null)
            return;

        if (_dayButtons.TryGetValue(_pendingFocusDate.Value, out var button))
            button.Focus(FocusState.Keyboard);

        _pendingFocusDate = null;
    }
}
