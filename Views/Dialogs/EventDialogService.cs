using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chronicle.Views.Dialogs;

/// <summary>
/// Builds and shows the Create/Edit Event dialogs, including the shared
/// form layout and save/delete handling. Calls <c>onChanged</c> after any
/// successful insert/update/delete so the caller can refresh the month view.
/// </summary>
internal sealed class EventDialogService
{
    private readonly EventRepository _eventRepository;
    private readonly CalendarRepository _calendarRepository;
    private readonly Func<XamlRoot> _getXamlRoot;
    private readonly Func<Task> _onChanged;

    public EventDialogService(
        EventRepository eventRepository,
        CalendarRepository calendarRepository,
        Func<XamlRoot> getXamlRoot,
        Func<Task> onChanged)
    {
        _eventRepository = eventRepository;
        _calendarRepository = calendarRepository;
        _getXamlRoot = getXamlRoot;
        _onChanged = onChanged;
    }

    // ── Shared dialog form ────────────────────────────────────────────────

    /// <summary>
    /// Builds the shared form panel used by both Create and Edit dialogs.
    /// Returns a getter delegate for the currently-selected calendar to avoid
    /// ref parameters (which cannot be captured inside lambdas).
    /// </summary>
    private static (StackPanel panel,
             TextBox titleBox,
             TimePicker startPicker,
             TimePicker endPicker,
             TextBlock errorBlock,
             Func<Calendar> getSelectedCalendar)
        BuildEventForm(
            List<Calendar> calendars,
            string initialTitle,
            TimeSpan initialStart,
            TimeSpan initialEnd,
            int initialCalendarIndex)
    {
        // Single-element array lets lambdas mutate the selected-calendar slot.
        var selectedHolder = new Calendar[] { calendars[initialCalendarIndex] };

        var contentPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 12
        };

        // Title
        var titleTextBox = new TextBox
        {
            PlaceholderText = "Event title",
            Text = initialTitle
        };
        contentPanel.Children.Add(new TextBlock { Text = "Title" });
        contentPanel.Children.Add(titleTextBox);

        // Calendar selection
        if (calendars.Count == 1)
        {
            contentPanel.Children.Add(new TextBlock
            {
                Text = $"Calendar: {calendars[0].Name}",
                FontSize = 14,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 })
            });
        }
        else
        {
            var calendarComboBox = new ComboBox
            {
                ItemsSource = calendars.Select(c => c.Name).ToList(),
                SelectedIndex = initialCalendarIndex
            };

            calendarComboBox.SelectionChanged += (s, e) =>
            {
                if (calendarComboBox.SelectedIndex >= 0)
                    selectedHolder[0] = calendars[calendarComboBox.SelectedIndex];
            };

            contentPanel.Children.Add(new TextBlock { Text = "Calendar" });
            contentPanel.Children.Add(calendarComboBox);
        }

        // Start time
        var startTimePicker = new TimePicker { Time = initialStart };
        contentPanel.Children.Add(new TextBlock { Text = "Start Time" });
        contentPanel.Children.Add(startTimePicker);

        // End time
        var endTimePicker = new TimePicker { Time = initialEnd };
        contentPanel.Children.Add(new TextBlock { Text = "End Time" });
        contentPanel.Children.Add(endTimePicker);

        var errorTextBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(
                new Windows.UI.Color { A = 255, R = 196, G = 43, B = 28 }),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        contentPanel.Children.Add(errorTextBlock);

        return (contentPanel, titleTextBox, startTimePicker, endTimePicker,
                errorTextBlock, () => selectedHolder[0]);
    }

    // ── Create Event dialog ───────────────────────────────────────────────

    public async Task ShowCreateEventDialogAsync(DateTime selectedDay, TimeSpan? startTime = null)
    {
        try
        {
            var calendars = await _calendarRepository.GetAllAsync();

            if (calendars.Count == 0)
            {
                await new ContentDialog
                {
                    Title = "No Calendars",
                    Content = "Please create a calendar before adding events.",
                    CloseButtonText = "OK",
                    XamlRoot = _getXamlRoot()
                }.ShowAsync();
                return;
            }

            var dialog = new ContentDialog
            {
                Title = "Create Event",
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                XamlRoot = _getXamlRoot()
            };

            // Default to 9–10am; Day View can pre-fill the double-clicked hour.
            var start = startTime ?? new TimeSpan(9, 0, 0);
            var end = start + TimeSpan.FromHours(1);

            var (panel, titleBox, startPicker, endPicker, errorBlock, getCalendar) =
                BuildEventForm(
                    calendars,
                    initialTitle: "",
                    initialStart: start,
                    initialEnd: end,
                    initialCalendarIndex: 0);

            dialog.Content = panel;

            dialog.PrimaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                errorBlock.Visibility = Visibility.Collapsed;

                try
                {
                    var title = titleBox.Text?.Trim();

                    if (string.IsNullOrEmpty(title))
                    {
                        e.Cancel = true;
                        errorBlock.Text = "Event title is required.";
                        errorBlock.Visibility = Visibility.Visible;
                        return;
                    }

                    var nowUtc = DateTime.UtcNow;

                    var newEvent = new Event
                    {
                        Id = Guid.NewGuid(),
                        CalendarId = getCalendar().Id,
                        Title = title,
                        StartTimeUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(selectedDay, startPicker.Time),
                        EndTimeUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(selectedDay, endPicker.Time),
                        Description = null,
                        IsAllDay = false,
                        RecurrenceRuleJson = null,
                        CreatedAtUtc = nowUtc,
                        UpdatedAtUtc = nowUtc
                    };

                    newEvent.Validate();
                    await _eventRepository.InsertAsync(newEvent);
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
                $"Error showing create event dialog: {ex.Message}\n{ex.StackTrace}");
        }
    }

    // ── Edit Event dialog ─────────────────────────────────────────────────

    public async Task ShowEditEventDialogAsync(Event evt)
    {
        try
        {
            var calendars = await _calendarRepository.GetAllAsync();

            var dialog = new ContentDialog
            {
                Title = "Edit Event",
                PrimaryButtonText = "Save",
                SecondaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                XamlRoot = _getXamlRoot()
            };

            int calendarIndex = Math.Max(0, calendars.FindIndex(c => c.Id == evt.CalendarId));

            var startLocal = evt.StartTimeUtc.ToLocalTime();
            var endLocal = evt.EndTimeUtc.ToLocalTime();
            var selectedDay = DateHelpers.GetLocalDayKey(startLocal);

            var (panel, titleBox, startPicker, endPicker, errorBlock, getCalendar) =
                BuildEventForm(
                    calendars,
                    initialTitle: evt.Title,
                    initialStart: startLocal.TimeOfDay,
                    initialEnd: endLocal.TimeOfDay,
                    initialCalendarIndex: calendarIndex);

            dialog.Content = panel;

            // Save
            dialog.PrimaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                errorBlock.Visibility = Visibility.Collapsed;

                try
                {
                    var title = titleBox.Text?.Trim();

                    if (string.IsNullOrEmpty(title))
                    {
                        e.Cancel = true;
                        errorBlock.Text = "Event title is required.";
                        errorBlock.Visibility = Visibility.Visible;
                        return;
                    }

                    evt.CalendarId = getCalendar().Id;
                    evt.Title = title;
                    evt.StartTimeUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(selectedDay, startPicker.Time);
                    evt.EndTimeUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(selectedDay, endPicker.Time);
                    evt.UpdatedAtUtc = DateTime.UtcNow;

                    evt.Validate();
                    await _eventRepository.UpdateAsync(evt);
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

            // Delete: two-step confirmation using the secondary button text,
            // because WinUI 3 only allows one ContentDialog open at a time.
            var deleteConfirmationRequested = false;

            dialog.SecondaryButtonClick += async (s, e) =>
            {
                var deferral = e.GetDeferral();
                dialog.IsSecondaryButtonEnabled = false;
                errorBlock.Visibility = Visibility.Collapsed;

                try
                {
                    if (!deleteConfirmationRequested)
                    {
                        deleteConfirmationRequested = true;
                        e.Cancel = true;
                        dialog.SecondaryButtonText = "Confirm Delete";
                        errorBlock.Text =
                            $"Click \"Confirm Delete\" to permanently delete \"{evt.Title}\".";
                        errorBlock.Visibility = Visibility.Visible;
                        return;
                    }

                    await _eventRepository.DeleteAsync(evt.Id);
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
                    dialog.IsSecondaryButtonEnabled = true;
                    deferral.Complete();
                }
            };

            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Error showing edit event dialog: {ex.Message}\n{ex.StackTrace}");
        }
    }
}
