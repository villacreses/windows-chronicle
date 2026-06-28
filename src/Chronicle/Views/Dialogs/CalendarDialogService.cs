using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chronicle.Views.Dialogs;

/// <summary>
/// Builds and shows the Create / Edit / Delete Calendar dialogs, including
/// the shared name + color-swatch form. Calls <c>onChanged</c> after any
/// successful insert/update/delete so the caller can reload calendars and
/// refresh the views.
/// </summary>
internal sealed class CalendarDialogService
{
    private readonly CalendarRepository _calendarRepository;
    private readonly EventRepository _eventRepository;
    private readonly Func<XamlRoot> _getXamlRoot;
    private readonly Func<Task> _onChanged;

    public CalendarDialogService(
        CalendarRepository calendarRepository,
        EventRepository eventRepository,
        Func<XamlRoot> getXamlRoot,
        Func<Task> onChanged)
    {
        _calendarRepository = calendarRepository;
        _eventRepository = eventRepository;
        _getXamlRoot = getXamlRoot;
        _onChanged = onChanged;
    }

    // ── Shared calendar form ──────────────────────────────────────────────

    /// <summary>
    /// Builds the shared form used by the Create and Edit dialogs. Returns a
    /// getter for the currently-selected color hex (a holder array lets the
    /// swatch lambdas mutate the selection without ref parameters).
    /// </summary>
    private static (StackPanel panel,
             TextBox nameBox,
             TextBlock errorBlock,
             Func<string> getSelectedColor)
        BuildCalendarForm(string initialName, string initialColorHex)
    {
        var selectedHolder = new[] { initialColorHex };

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 12
        };

        // Name
        var nameTextBox = new TextBox
        {
            PlaceholderText = "Calendar name",
            Text = initialName
        };
        contentPanel.Children.Add(new TextBlock { Text = "Name" });
        contentPanel.Children.Add(nameTextBox);

        // Color swatches
        contentPanel.Children.Add(new TextBlock { Text = "Color" });

        var swatchPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };

        var swatches = new List<(Button button, string hex)>();
        foreach (var hex in ColorHelper.Palette)
        {
            var capturedHex = hex;
            var button = new Button
            {
                Width = 32,
                Height = 32,
                MinWidth = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(ColorHelper.ParseHexColor(capturedHex)),
                BorderBrush = new SolidColorBrush(ColorHelper.AppAccent),
                BorderThickness = new Thickness(
                    string.Equals(capturedHex, initialColorHex, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
            };

            button.Click += (s, e) =>
            {
                selectedHolder[0] = capturedHex;
                foreach (var (b, h) in swatches)
                    b.BorderThickness = new Thickness(
                        string.Equals(h, capturedHex, StringComparison.OrdinalIgnoreCase) ? 3 : 0);
            };

            swatches.Add((button, capturedHex));
            swatchPanel.Children.Add(button);
        }

        contentPanel.Children.Add(swatchPanel);

        var errorTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(
                new Windows.UI.Color { A = 255, R = 196, G = 43, B = 28 }),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        contentPanel.Children.Add(errorTextBlock);

        return (contentPanel, nameTextBox, errorTextBlock, () => selectedHolder[0]);
    }

    // ── Create Calendar ───────────────────────────────────────────────────

    public async Task ShowCreateCalendarDialogAsync()
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "New Calendar",
                PrimaryButtonText = "Create",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _getXamlRoot()
            };

            var (panel, nameBox, errorBlock, getColor) =
                BuildCalendarForm(initialName: "", initialColorHex: ColorHelper.AppAccentHex);

            dialog.Content = panel;

            dialog.PrimaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                errorBlock.Visibility = Visibility.Collapsed;

                try
                {
                    var name = nameBox.Text?.Trim();

                    if (string.IsNullOrEmpty(name))
                    {
                        e.Cancel = true;
                        errorBlock.Text = "Calendar name is required.";
                        errorBlock.Visibility = Visibility.Visible;
                        return;
                    }

                    var calendar = new Calendar
                    {
                        Id = Guid.NewGuid(),
                        Name = name,
                        Color = getColor()
                    };

                    await _calendarRepository.InsertAsync(calendar);
                    await _onChanged();
                }
                catch (Exception ex)
                {
                    e.Cancel = true;
                    errorBlock.Text = "Couldn't save calendar. Please try again.";
                    errorBlock.Visibility = Visibility.Visible;
                    System.Diagnostics.Debug.WriteLine(ex);
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error showing create calendar dialog: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Edit Calendar ─────────────────────────────────────────────────────

    public async Task ShowEditCalendarDialogAsync(Calendar calendar)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Edit Calendar",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = _getXamlRoot()
            };

            var (panel, nameBox, errorBlock, getColor) =
                BuildCalendarForm(initialName: calendar.Name, initialColorHex: calendar.Color);

            dialog.Content = panel;

            dialog.PrimaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                errorBlock.Visibility = Visibility.Collapsed;

                try
                {
                    var name = nameBox.Text?.Trim();

                    if (string.IsNullOrEmpty(name))
                    {
                        e.Cancel = true;
                        errorBlock.Text = "Calendar name is required.";
                        errorBlock.Visibility = Visibility.Visible;
                        return;
                    }

                    calendar.Name = name;
                    calendar.Color = getColor();

                    await _calendarRepository.UpdateAsync(calendar);
                    await _onChanged();
                }
                catch (Exception ex)
                {
                    e.Cancel = true;
                    errorBlock.Text = ex.Message;
                    errorBlock.Visibility = Visibility.Visible;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error showing edit calendar dialog: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Delete Calendar ───────────────────────────────────────────────────

    /// <summary>
    /// Confirms deletion, surfacing how many events will be removed along
    /// with the calendar (events are cascade-deleted — see DECISIONS.md).
    /// </summary>
    public async Task ShowDeleteCalendarDialogAsync(Calendar calendar)
    {
        try
        {
            var eventCount = await _eventRepository.CountByCalendarAsync(calendar.Id);

            var message = eventCount == 0
                ? $"Delete the calendar \"{calendar.Name}\"? This can't be undone."
                : $"Delete the calendar \"{calendar.Name}\" and its {eventCount} " +
                  $"event{(eventCount == 1 ? "" : "s")}? This can't be undone.";

            var dialog = new ContentDialog
            {
                Title = "Delete Calendar",
                Content = message,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = _getXamlRoot()
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
                return;

            await _calendarRepository.DeleteAsync(calendar.Id);
            await _onChanged();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error deleting calendar: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
