using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Small shared rendering primitives for calendar views. Renderers still own
/// layout, headers, event limits, and interaction policy; this helper keeps
/// repeated chip and selectable-day visuals in one place.
/// </summary>
internal static class CalendarRenderHelper
{
    public static readonly Windows.UI.Color MutedText =
        new() { A = 255, R = 100, G = 100, B = 100 };

    public static readonly Windows.UI.Color OverflowText =
        new() { A = 200, R = 128, G = 128, B = 128 };

    private static readonly Windows.UI.Color DayBackground =
        new() { A = 255, R = 255, G = 255, B = 255 };

    private static readonly Windows.UI.Color OutOfScopeBackground =
        new() { A = 255, R = 245, G = 245, B = 245 };

    private static readonly Windows.UI.Color SelectedBackground =
        new() { A = 255, R = 239, G = 246, B = 255 };

    private static readonly Windows.UI.Color CellBorder =
        new() { A = 200, R = 220, G = 220, B = 220 };

    private static readonly Windows.UI.Color DayNumberText =
        new() { A = 255, R = 0, G = 0, B = 0 };

    public static Border CreateEventChip(
        Event evt,
        IEnumerable<Calendar> calendars,
        string text,
        Action<Event, FrameworkElement> onEventClicked,
        Thickness textMargin = default)
    {
        var capturedEvt = evt;
        var calColor = ColorHelper.ResolveCalendarColor(calendars, capturedEvt.CalendarId);

        var chipText = new TextBlock
        {
            Text = text,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(calColor),
            Margin = textMargin
        };

        var chip = new Border
        {
            Child = chipText,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent)
        };

        chip.Tapped += (s, e) =>
        {
            e.Handled = true;
            onEventClicked(capturedEvt, chip);
        };
        chip.DoubleTapped += (s, e) => e.Handled = true;

        return chip;
    }

    public static void ApplyDayContainerVisuals(
        Border border,
        bool isSelected,
        bool isInScope = true)
    {
        border.Background = new SolidColorBrush(
            isSelected ? SelectedBackground : isInScope ? DayBackground : OutOfScopeBackground);
        border.BorderBrush = new SolidColorBrush(isSelected ? ColorHelper.AppAccent : CellBorder);
        border.BorderThickness = new Thickness(isSelected ? 2 : 1);
    }

    public static void ApplyDayNumberVisuals(TextBlock dayNumber, bool isSelected)
    {
        dayNumber.Foreground = new SolidColorBrush(isSelected ? ColorHelper.AppAccent : DayNumberText);
        dayNumber.FontWeight = isSelected ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold;
    }
}
