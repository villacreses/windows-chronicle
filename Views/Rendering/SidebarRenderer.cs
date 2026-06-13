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
/// Builds the Calendars section of the sidebar: a header with an "add"
/// action, a "no calendars" placeholder, and a row per calendar with a
/// visibility checkbox plus an overflow menu for Edit/Delete.
/// </summary>
internal sealed class SidebarRenderer
{
    // Segoe MDL2 Assets glyphs (escaped to keep the source ASCII-clean).
    private const string AddGlyph = "";  // Add
    private const string MoreGlyph = ""; // More (horizontal ellipsis)

    private static readonly Windows.UI.Color HeaderText =
        new() { A = 255, R = 100, G = 100, B = 100 };

    private static readonly Windows.UI.Color PlaceholderText =
        new() { A = 180, R = 120, G = 120, B = 120 };

    private readonly StackPanel _sidebarPanel;

    public SidebarRenderer(StackPanel sidebarPanel)
    {
        _sidebarPanel = sidebarPanel;
    }

    /// <summary>
    /// Renders the sidebar from the given calendars and visibility map.
    /// <paramref name="onVisibilityToggled"/> fires with the calendar id and
    /// new visibility state when a checkbox is toggled.
    /// <paramref name="onAddCalendar"/>, <paramref name="onEditCalendar"/>,
    /// and <paramref name="onDeleteCalendar"/> drive calendar management.
    /// </summary>
    public void Render(
        List<Calendar> calendars,
        Dictionary<Guid, bool> calendarVisibility,
        Action<Guid, bool> onVisibilityToggled,
        Action onAddCalendar,
        Action<Calendar> onEditCalendar,
        Action<Calendar> onDeleteCalendar)
    {
        _sidebarPanel.Children.Clear();
        _sidebarPanel.Children.Add(BuildHeader(onAddCalendar));

        if (calendars.Count == 0)
        {
            _sidebarPanel.Children.Add(new TextBlock
            {
                Text = "No calendars yet.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(PlaceholderText)
            });
            return;
        }

        foreach (var cal in calendars)
        {
            _sidebarPanel.Children.Add(BuildCalendarRow(
                cal, calendarVisibility, onVisibilityToggled, onEditCalendar, onDeleteCalendar));
        }
    }

    private static Grid BuildHeader(Action onAddCalendar)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = "Calendars",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(HeaderText),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(label, 0);
        header.Children.Add(label);

        var addButton = new Button
        {
            Content = new FontIcon
            {
                Glyph = AddGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12
            },
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0)
        };
        ToolTipService.SetToolTip(addButton, "New calendar");
        addButton.Click += (s, e) => onAddCalendar();
        Grid.SetColumn(addButton, 1);
        header.Children.Add(addButton);

        return header;
    }

    private static Grid BuildCalendarRow(
        Calendar cal,
        Dictionary<Guid, bool> calendarVisibility,
        Action<Guid, bool> onVisibilityToggled,
        Action<Calendar> onEditCalendar,
        Action<Calendar> onDeleteCalendar)
    {
        var capturedId = cal.Id;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
            Padding = new Thickness(4, 6, 4, 6),
            VerticalAlignment = VerticalAlignment.Center
        };
        checkBox.Checked += (s, e) => onVisibilityToggled(capturedId, true);
        checkBox.Unchecked += (s, e) => onVisibilityToggled(capturedId, false);
        Grid.SetColumn(checkBox, 0);
        row.Children.Add(checkBox);

        var moreButton = BuildOverflowButton(cal, onEditCalendar, onDeleteCalendar);
        Grid.SetColumn(moreButton, 1);
        row.Children.Add(moreButton);

        return row;
    }

    private static Button BuildOverflowButton(
        Calendar cal,
        Action<Calendar> onEditCalendar,
        Action<Calendar> onDeleteCalendar)
    {
        var menu = new MenuFlyout();

        var editItem = new MenuFlyoutItem { Text = "Edit" };
        editItem.Click += (s, e) => onEditCalendar(cal);
        menu.Items.Add(editItem);

        var deleteItem = new MenuFlyoutItem { Text = "Delete" };
        deleteItem.Click += (s, e) => onDeleteCalendar(cal);
        menu.Items.Add(deleteItem);

        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = MoreGlyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12
            },
            Padding = new Thickness(6, 2, 6, 2),
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Flyout = menu
        };
        ToolTipService.SetToolTip(button, $"Manage \"{cal.Name}\"");

        return button;
    }
}
