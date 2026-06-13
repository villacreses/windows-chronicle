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
/// Builds the Calendars section of the sidebar: section header, "no calendars"
/// placeholder, and a checkbox row per calendar for visibility toggling.
/// </summary>
internal sealed class SidebarRenderer
{
    private readonly StackPanel _sidebarPanel;

    public SidebarRenderer(StackPanel sidebarPanel)
    {
        _sidebarPanel = sidebarPanel;
    }

    /// <summary>
    /// Renders the sidebar from the given calendars and visibility map.
    /// <paramref name="onVisibilityToggled"/> is invoked with the calendar id
    /// and new visibility state whenever a checkbox is checked/unchecked.
    /// </summary>
    public void Render(
        List<Calendar> calendars,
        Dictionary<Guid, bool> calendarVisibility,
        Action<Guid, bool> onVisibilityToggled)
    {
        _sidebarPanel.Children.Clear();

        _sidebarPanel.Children.Add(new TextBlock
        {
            Text = "Calendars",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(
                new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 }),
            Margin = new Thickness(0, 0, 0, 8)
        });

        if (calendars.Count == 0)
        {
            _sidebarPanel.Children.Add(new TextBlock
            {
                Text = "No calendars yet.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 180, R = 120, G = 120, B = 120 })
            });
            return;
        }

        foreach (var cal in calendars)
        {
            var capturedId = cal.Id;

            var colorDot = new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(ColorHelper.ParseHexColor(cal.Color)),
                VerticalAlignment = VerticalAlignment.Center
            };

            var nameBlock = new TextBlock
            {
                Text = cal.Name,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var rowContent = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowContent.Children.Add(colorDot);
            rowContent.Children.Add(nameBlock);

            var checkBox = new CheckBox
            {
                Content = rowContent,
                IsChecked = calendarVisibility.GetValueOrDefault(capturedId, true),
                Padding = new Thickness(4, 6, 4, 6)
            };

            checkBox.Checked += (s, e) => onVisibilityToggled(capturedId, true);
            checkBox.Unchecked += (s, e) => onVisibilityToggled(capturedId, false);

            _sidebarPanel.Children.Add(checkBox);
        }
    }
}
