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
/// Small shared rendering primitives for the calendar views, styled to the
/// dark "Fluent" design (see <see cref="Theme"/>). Renderers still own layout,
/// headers, event limits, and interaction policy; this helper keeps repeated
/// chip and selectable-day visuals in one place so Month and Week stay
/// consistent.
/// </summary>
internal static class CalendarRenderHelper
{
    /// <summary>
    /// Fixed pill height. Chips are forced to this height so a renderer can
    /// compute how many fit in a cell from a simple pitch
    /// (<see cref="ChipHeight"/> + <see cref="ChipSpacing"/>) without measuring
    /// each chip.
    /// </summary>
    public const double ChipHeight = 21;

    /// <summary>Vertical gap between stacked chips in a day cell.</summary>
    public const double ChipSpacing = 3;

    /// <summary>Secondary label color (day-of-week headers, agenda meta).</summary>
    public static Windows.UI.Color MutedText => Theme.Text3;

    /// <summary>"+N more" overflow indicator color.</summary>
    public static Windows.UI.Color OverflowText => Theme.Text3;

    /// <summary>
    /// Creates a filled "pill" event chip: a soft, calendar-tinted background
    /// with lightened calendar-colored text. Single tap opens the popover (via
    /// <paramref name="onEventClicked"/>); both tap gestures are marked handled
    /// so they don't bubble to the day cell's select/create handlers.
    /// </summary>
    public static Border CreateEventChip(
        Event evt,
        IEnumerable<Calendar> calendars,
        string text,
        Action<Event, FrameworkElement> onEventClicked)
    {
        var capturedEvt = evt;
        var calColor = ColorHelper.ResolveCalendarColor(calendars, capturedEvt.CalendarId);

        var chipText = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(ColorHelper.LightenForText(calColor)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var chip = new Border
        {
            Child = chipText,
            Background = new SolidColorBrush(ColorHelper.Soften(calColor)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Height = ChipHeight
        };

        chip.Tapped += (s, e) =>
        {
            e.Handled = true;
            onEventClicked(capturedEvt, chip);
        };
        chip.DoubleTapped += (s, e) => e.Handled = true;

        return chip;
    }

    /// <summary>
    /// Creates a chip-styled "+N more" overflow indicator that matches the
    /// event-pill shape/size with neutral colors. Tapping it runs
    /// <paramref name="onSelectDay"/> (the existing day-selection behavior);
    /// gestures are marked handled so the day cell's create-on-double-tap does
    /// not also fire.
    /// </summary>
    public static Border CreateOverflowChip(int hiddenCount, Action onSelectDay)
    {
        var text = new TextBlock
        {
            Text = $"+{hiddenCount} more",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(Theme.Text2),
            VerticalAlignment = VerticalAlignment.Center
        };

        var chip = new Border
        {
            Child = text,
            Background = new SolidColorBrush(Theme.Hairline2),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Height = ChipHeight
        };

        chip.Tapped += (s, e) =>
        {
            e.Handled = true;
            onSelectDay();
        };
        chip.DoubleTapped += (s, e) => e.Handled = true;

        return chip;
    }

    /// <summary>
    /// Applies the day-cell container visuals: selected days get a soft accent
    /// tint and accent ring; in-scope days get the cell surface; out-of-scope
    /// (adjacent-month) days dim to the recessed surface. Unselected cells use
    /// a hairline border so the grid reads as thin lines on dark.
    /// </summary>
    public static void ApplyDayContainerVisuals(
        Border border,
        bool isSelected,
        bool isInScope = true)
    {
        border.Background = new SolidColorBrush(
            isSelected ? Theme.AccentSoft : isInScope ? Theme.Cell : Theme.CellOut);
        border.BorderBrush = new SolidColorBrush(isSelected ? Theme.Accent : Theme.Hairline2);
        border.BorderThickness = new Thickness(isSelected ? 1.5 : 1);
    }

    /// <summary>
    /// Builds the circular date-number badge (a <see cref="Border"/> wrapping a
    /// <see cref="TextBlock"/>) used by both views. Today fills the circle with
    /// the accent; selected (non-today) days use accent text. Call
    /// <see cref="ApplyDayNumberVisuals"/> to (re)apply state.
    /// </summary>
    public static Border CreateDayNumber(string text, double size, double fontSize, out TextBlock numberText)
    {
        numberText = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Child = numberText
        };
    }

    public static void ApplyDayNumberVisuals(
        Border circle,
        TextBlock numberText,
        bool isSelected,
        bool isToday,
        bool isInScope = true)
    {
        if (isToday)
        {
            circle.Background = new SolidColorBrush(Theme.Accent);
            numberText.Foreground = new SolidColorBrush(Theme.OnAccent);
            numberText.FontWeight = FontWeights.Bold;
            return;
        }

        circle.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        numberText.Foreground = new SolidColorBrush(
            isSelected ? Theme.AccentText : isInScope ? Theme.Text : Theme.Text4);
        numberText.FontWeight = isSelected ? FontWeights.Bold : FontWeights.SemiBold;
    }
}
