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
/// Renders the search results panel (hosted as the content of a
/// <see cref="Microsoft.UI.Xaml.Controls.Flyout"/> anchored to the header
/// search box). Rows are chronological — the order is set by
/// <see cref="Projection.EventProjection.SearchOccurrences"/>, which sorts
/// by <c>StartTimeUtc</c>. Each row is a full-width <see cref="Button"/>
/// whose click opens the edit popover directly, matching the
/// <see cref="SelectedDayRenderer"/> interaction shape.
///
/// The renderer owns no state beyond the container and the activation
/// callback captured at construction. All events are handed to
/// <see cref="Render"/>; the panel does not query and does not cache.
/// </summary>
internal sealed class SearchResultsRenderer
{
    private readonly StackPanel _container;
    private readonly Action<Event, FrameworkElement> _onActivate;

    public SearchResultsRenderer(
        StackPanel container,
        Action<Event, FrameworkElement> onActivate)
    {
        _container = container;
        _onActivate = onActivate;
    }

    /// <summary>
    /// Renders <paramref name="results"/> (already ordered by
    /// <c>StartTimeUtc</c>). Empty input shows a muted "No results." block.
    /// </summary>
    public void Render(List<Event> results, List<Calendar> calendars)
    {
        _container.Children.Clear();

        if (results.Count == 0)
        {
            _container.Children.Add(new TextBlock
            {
                Text = "No results.",
                FontSize = 13,
                Foreground = new SolidColorBrush(CalendarRenderHelper.MutedText),
                Margin = new Thickness(6, 6, 6, 6)
            });
            return;
        }

        foreach (var evt in results)
            _container.Children.Add(BuildResultRow(evt, calendars, _onActivate));
    }

    private static Button BuildResultRow(
        Event evt,
        List<Calendar> calendars,
        Action<Event, FrameworkElement> onActivate)
    {
        var capturedEvt = evt;

        var colorBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(
                ColorHelper.ResolveCalendarColor(calendars, capturedEvt.CalendarId)),
            VerticalAlignment = VerticalAlignment.Stretch,
            MinHeight = 34
        };

        var whenBlock = new TextBlock
        {
            Text = FormatWhen(capturedEvt),
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
        textColumn.Children.Add(whenBlock);
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
        row.Click += (s, e) => onActivate(capturedEvt, (FrameworkElement)s);

        return row;
    }

    private static string FormatWhen(Event evt)
    {
        var startLocal = evt.StartTimeUtc.ToLocalTime();
        var date = startLocal.ToString("ddd, MMM d, yyyy");

        if (evt.IsAllDay)
            return $"{date} · All day";

        var time = startLocal.Minute == 0
            ? startLocal.ToString("h tt")
            : startLocal.ToString("h:mm tt");
        return $"{date} · {time}";
    }
}
