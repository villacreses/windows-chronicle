using Chronicle.Helpers;
using Chronicle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;

namespace Chronicle.Views.Popovers;

/// <summary>
/// Lightweight read-only summary card for a single event, shown inside a
/// <see cref="Microsoft.UI.Xaml.Controls.Flyout"/> anchored to the clicked
/// event chip. Offers "Edit" (opens the full edit dialog via
/// <see cref="Chronicle.Views.Dialogs.EventDialogService"/>) and "Delete"
/// (two-step confirmation, mirroring the edit dialog's delete flow).
/// </summary>
public sealed partial class EventPopover : UserControl
{
    /// <summary>Raised when the user clicks "Edit" for the currently displayed event.</summary>
    public event EventHandler<Event>? EditRequested;

    /// <summary>Raised when the user confirms deletion of the currently displayed event.</summary>
    public event EventHandler<Event>? DeleteRequested;

    /// <summary>Raised when the user clicks "Close".</summary>
    public event EventHandler? CloseRequested;

    private Event? _currentEvent;
    private bool _deleteConfirmationRequested;

    public EventPopover()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Populates the popover for the given event/calendar pair. Resets any
    /// pending delete confirmation from a previous event.
    /// </summary>
    public void SetEvent(Event evt, Calendar? calendar)
    {
        _currentEvent = evt;
        _deleteConfirmationRequested = false;
        DeleteButton.Content = "Delete";
        StatusText.Visibility = Visibility.Collapsed;

        TitleText.Text = evt.Title;

        var dotColor = calendar is not null
            ? ColorHelper.ParseHexColor(calendar.Color)
            : ColorHelper.AppAccent;
        ColorDot.Background = new SolidColorBrush(dotColor);

        TimeRangeText.Text = evt.IsAllDay
            ? "All day"
            : FormatTimeRange(evt.StartTimeUtc, evt.EndTimeUtc);

        // Event has no Location field yet; left collapsed until the model
        // grows one. See ARCHITECTURE/DECISIONS for the recommended follow-up.
        LocationText.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(evt.Description))
        {
            DescriptionText.Text = evt.Description;
            DescriptionText.Visibility = Visibility.Visible;
        }
        else
        {
            DescriptionText.Visibility = Visibility.Collapsed;
        }
    }

    private static string FormatTimeRange(DateTime startUtc, DateTime endUtc)
    {
        var startLocal = startUtc.ToLocalTime();
        var endLocal = endUtc.ToLocalTime();
        return $"{startLocal:h:mm tt} – {endLocal:h:mm tt}";
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEvent is not null)
            EditRequested?.Invoke(this, _currentEvent);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEvent is null)
            return;

        // Two-step confirmation, mirroring EventDialogService's delete flow.
        if (!_deleteConfirmationRequested)
        {
            _deleteConfirmationRequested = true;
            DeleteButton.Content = "Confirm Delete";
            StatusText.Text = $"Click \"Confirm Delete\" to permanently delete \"{_currentEvent.Title}\".";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        DeleteRequested?.Invoke(this, _currentEvent);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
