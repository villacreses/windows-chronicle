using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chronicle.Views.Popovers;

/// <summary>
/// A light-dismiss create/edit event editor shown in a <see cref="Flyout"/>
/// anchored to a <see cref="FrameworkElement"/> in the calendar surface
/// (typically the clicked event chip or the day cell / time slot a draft
/// chip is sitting on). This is the sole event-editing UI; the previous
/// modal `EventDialogService` has been removed.
///
/// The form is built programmatically (Name, Calendar, Start date+time, End
/// date+time, Save/Cancel). <see cref="ShowCreateEventAsync"/> /
/// <see cref="ShowEditEventAsync"/> return the resulting <see cref="Event"/> on
/// save, or <c>null</c> if the popover is cancelled or light-dismissed.
///
/// Placement defaults to <see cref="FlyoutPlacementMode.RightEdgeAlignedTop"/>,
/// matching the read-only <see cref="EventPopover"/> so the editor reads as a
/// continuation of the same talk-bubble. Callers can override it — Day View
/// uses <see cref="FlyoutPlacementMode.Top"/>, which centers the form over its
/// full-width chip (i.e. on the main section), since edge-aligning against a
/// full-width Day chip would cram the form against the window edge and overflow
/// its contents. Anchoring to a small element (rather than the window content)
/// also keeps the flyout's light-dismiss capture region off the scrollbar.
///
/// The popover performs no persistence — it only constructs and returns the
/// <see cref="Event"/>; the caller saves it via the event repository.
/// </summary>
public static class EventEditPopover
{
    private const double FormWidth = 340;

    /// <summary>
    /// Shows the create-event popover anchored to <paramref name="anchorElement"/>
    /// (typically the freshly-rendered draft chip, or the cell / column that
    /// was tapped). The form defaults to <paramref name="suggestedStartTime"/>
    /// for one hour, the first available calendar selected. Returns the new
    /// <see cref="Event"/> on save, or <c>null</c> if dismissed without saving.
    /// </summary>
    public static Task<Event?> ShowCreateEventAsync(
        FrameworkElement anchorElement,
        DateTime suggestedStartTime,
        IList<Calendar> availableCalendars,
        FlyoutPlacementMode placement = FlyoutPlacementMode.RightEdgeAlignedTop)
    {
        return ShowAsync(
            anchorElement,
            placement,
            heading: "Create Event",
            initialTitle: "",
            initialStartLocal: suggestedStartTime,
            initialEndLocal: suggestedStartTime.AddHours(1),
            calendars: availableCalendars,
            selectedCalendarId: availableCalendars.Count > 0 ? availableCalendars[0].Id : null,
            buildEvent: (title, calendarId, startUtc, endUtc) =>
            {
                var nowUtc = DateTime.UtcNow;
                return new Event
                {
                    Id = Guid.NewGuid(),
                    CalendarId = calendarId,
                    Title = title,
                    StartTimeUtc = startUtc,
                    EndTimeUtc = endUtc,
                    Description = null,
                    IsAllDay = false,
                    RecurrenceRule = null,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                };
            });
    }

    /// <summary>
    /// Shows the edit-event popover anchored to <paramref name="anchorElement"/>
    /// (typically the event chip the user is editing), pre-filled from
    /// <paramref name="eventToEdit"/>. Fields not on the form (Id, Description,
    /// IsAllDay, RecurrenceRule, RecurrenceExDatesUtc, RecurrenceEndUtcCached,
    /// CreatedAtUtc) are preserved;
    /// <c>UpdatedAtUtc</c> is refreshed. Returns the edited <see cref="Event"/>
    /// on save, or <c>null</c> if dismissed without saving.
    /// </summary>
    public static Task<Event?> ShowEditEventAsync(
        FrameworkElement anchorElement,
        Event eventToEdit,
        IList<Calendar> availableCalendars,
        FlyoutPlacementMode placement = FlyoutPlacementMode.RightEdgeAlignedTop)
    {
        return ShowAsync(
            anchorElement,
            placement,
            heading: "Edit Event",
            initialTitle: eventToEdit.Title,
            initialStartLocal: eventToEdit.StartTimeUtc.ToLocalTime(),
            initialEndLocal: eventToEdit.EndTimeUtc.ToLocalTime(),
            calendars: availableCalendars,
            selectedCalendarId: eventToEdit.CalendarId,
            buildEvent: (title, calendarId, startUtc, endUtc) => new Event
            {
                Id = eventToEdit.Id,
                CalendarId = calendarId,
                Title = title,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                Description = eventToEdit.Description,
                IsAllDay = eventToEdit.IsAllDay,
                RecurrenceRule = eventToEdit.RecurrenceRule,
                RecurrenceExDatesUtc = eventToEdit.RecurrenceExDatesUtc,
                RecurrenceEndUtcCached = eventToEdit.RecurrenceEndUtcCached,
                CreatedAtUtc = eventToEdit.CreatedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow
            });
    }

    // ── Shared implementation ─────────────────────────────────────────────

    /// <summary>
    /// Builds the form, wires the flyout, and bridges the non-awaitable flyout
    /// to a <see cref="Task{Event}"/> via a <see cref="TaskCompletionSource{T}"/>.
    /// <paramref name="buildEvent"/> receives the validated form values
    /// (title, calendar id, start UTC, end UTC) and produces the create- or
    /// edit-flavored <see cref="Event"/>.
    /// </summary>
    private static Task<Event?> ShowAsync(
        FrameworkElement anchorElement,
        FlyoutPlacementMode placement,
        string heading,
        string initialTitle,
        DateTime initialStartLocal,
        DateTime initialEndLocal,
        IList<Calendar> calendars,
        Guid? selectedCalendarId,
        Func<string, Guid, DateTime, DateTime, Event> buildEvent)
    {
        var tcs = new TaskCompletionSource<Event?>();

        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = FormWidth,
            Spacing = 10
        };

        root.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Theme.Text),
            Margin = new Thickness(0, 0, 0, 2)
        });

        // Name
        var nameBox = new TextBox
        {
            PlaceholderText = "Event title",
            Text = initialTitle,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(MakeLabel("Name"));
        root.Children.Add(nameBox);

        // Calendar
        var calendarCombo = new ComboBox
        {
            ItemsSource = calendars,
            DisplayMemberPath = nameof(Calendar.Name),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        if (selectedCalendarId is Guid wanted)
        {
            var match = calendars.FirstOrDefault(c => c.Id == wanted);
            calendarCombo.SelectedItem = match ?? calendars.FirstOrDefault();
        }
        else if (calendars.Count > 0)
        {
            calendarCombo.SelectedIndex = 0;
        }
        root.Children.Add(MakeLabel("Calendar"));
        root.Children.Add(calendarCombo);

        // Start date + time
        var startDate = new DatePicker { Date = new DateTimeOffset(initialStartLocal) };
        var startTime = new TimePicker { Time = initialStartLocal.TimeOfDay };
        root.Children.Add(MakeLabel("Start"));
        root.Children.Add(MakeDateTimeRow(startDate, startTime));

        // End date + time
        var endDate = new DatePicker { Date = new DateTimeOffset(initialEndLocal) };
        var endTime = new TimePicker { Time = initialEndLocal.TimeOfDay };
        root.Children.Add(MakeLabel("End"));
        root.Children.Add(MakeDateTimeRow(endDate, endTime));

        // Inline error (hidden until validation fails).
        var errorBlock = new TextBlock
        {
            Foreground = new SolidColorBrush(Theme.Danger),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(errorBlock);

        // Buttons
        var saveButton = new Button
        {
            Content = "Save",
            Style = Application.Current.Resources["AccentButtonStyle"] as Style
        };
        var cancelButton = new Button { Content = "Cancel" };

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        buttonRow.Children.Add(saveButton);
        buttonRow.Children.Add(cancelButton);
        root.Children.Add(buttonRow);

        // Placement is caller-chosen (default RightEdgeAlignedTop). Anchoring to
        // a small element (not Content) keeps the flyout's light-dismiss capture
        // region off the scrollbar.
        var flyout = new Flyout
        {
            Content = root,
            Placement = placement
        };

        saveButton.Click += (s, e) =>
        {
            if (TryBuildEvent(
                    nameBox, calendarCombo, startDate, startTime, endDate, endTime,
                    buildEvent, errorBlock, out var result))
            {
                // Set the result before hiding so the Closed handler (which fires
                // on Hide) can't race a null over a successful save.
                tcs.TrySetResult(result);
                flyout.Hide();
            }
        };

        cancelButton.Click += (s, e) =>
        {
            tcs.TrySetResult(null);
            flyout.Hide();
        };

        // Light-dismiss (clicking outside) closes the flyout without a save.
        flyout.Closed += (s, e) => tcs.TrySetResult(null);

        flyout.ShowAt(anchorElement);

        return tcs.Task;
    }

    /// <summary>
    /// Validates the form and, if valid, builds the <see cref="Event"/> via
    /// <paramref name="buildEvent"/>. On any failure it populates
    /// <paramref name="errorBlock"/>, leaves the popover open, and returns false.
    /// </summary>
    private static bool TryBuildEvent(
        TextBox nameBox,
        ComboBox calendarCombo,
        DatePicker startDate,
        TimePicker startTime,
        DatePicker endDate,
        TimePicker endTime,
        Func<string, Guid, DateTime, DateTime, Event> buildEvent,
        TextBlock errorBlock,
        out Event? result)
    {
        result = null;

        var title = nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
            return Fail(errorBlock, "Event name is required.");

        if (calendarCombo.SelectedItem is not Calendar calendar)
            return Fail(errorBlock, "Please select a calendar.");

        var startUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(startDate.Date.Date, startTime.Time);
        var endUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(endDate.Date.Date, endTime.Time);

        if (startUtc >= endUtc)
            return Fail(errorBlock, "End time must be after start time.");

        try
        {
            var evt = buildEvent(title, calendar.Id, startUtc, endUtc);
            evt.Validate(); // defensive: enforces UTC kind + end >= start
            result = evt;
            errorBlock.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            return Fail(errorBlock, ex.Message);
        }
    }

    private static bool Fail(TextBlock errorBlock, string message)
    {
        errorBlock.Text = message;
        errorBlock.Visibility = Visibility.Visible;
        return false;
    }

    // ── Form building helpers ─────────────────────────────────────────────

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = new SolidColorBrush(Theme.Text2),
        Margin = new Thickness(0, 2, 0, 0)
    };

    /// <summary>
    /// Lays a date picker and a time picker side by side (date wider than time)
    /// so the pair scales with the form width.
    /// </summary>
    private static Grid MakeDateTimeRow(DatePicker datePicker, TimePicker timePicker)
    {
        datePicker.HorizontalAlignment = HorizontalAlignment.Stretch;
        timePicker.HorizontalAlignment = HorizontalAlignment.Stretch;

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });

        datePicker.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(datePicker, 0);
        Grid.SetColumn(timePicker, 1);
        row.Children.Add(datePicker);
        row.Children.Add(timePicker);
        return row;
    }
}
