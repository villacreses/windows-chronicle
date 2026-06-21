using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
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
/// chip is sitting on). This is the sole event-editing UI.
///
/// The form is built programmatically (Name, Calendar, Start, End, Repeats,
/// Save/Cancel). <see cref="ShowCreateEventAsync"/> /
/// <see cref="ShowEditEventAsync"/> return the resulting <see cref="Event"/> on
/// save, or <c>null</c> if the popover is cancelled or light-dismissed.
///
/// Recurring-event semantics (Phase 1): editing a series here updates the
/// master row in place. A banner above the form names this. The "this event
/// only" scope is deferred to Phase 2 (see DECISIONS.md). The popover never
/// receives an occurrence — callers load the master before invoking
/// <see cref="ShowEditEventAsync"/>; this matches the persistence boundary
/// enforced by <c>EventRepository.RefuseOccurrence</c>.
///
/// The popover performs no persistence — it only constructs and returns the
/// <see cref="Event"/>; the caller saves it via the event repository.
/// </summary>
public static class EventEditPopover
{
    private const double FormWidth = 340;

    // Cached theme brushes — the form is rebuilt programmatically on every
    // show, and these were the only allocations that needed to be (6 fresh
    // SolidColorBrush instances per popover open, against an otherwise
    // identical visual). Brushes are immutable in practice for our usage.
    private static readonly SolidColorBrush TextBrush     = new(Theme.Text);
    private static readonly SolidColorBrush Text2Brush    = new(Theme.Text2);
    private static readonly SolidColorBrush ElevatedBrush = new(Theme.Elevated);
    private static readonly SolidColorBrush HairlineBrush = new(Theme.Hairline);
    private static readonly SolidColorBrush DangerBrush   = new(Theme.Danger);

    /// <summary>
    /// Shows the create-event popover anchored to <paramref name="anchorElement"/>.
    /// Defaults: <paramref name="suggestedStartTime"/> for one hour, the first
    /// available calendar selected, does-not-repeat. Returns the new
    /// <see cref="Event"/> on save, or <c>null</c> if dismissed.
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
            initialRecurrence: null,
            showRecurringBanner: false,
            buildEvent: (title, calendarId, startUtc, endUtc, recurrence) =>
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
                    RecurrenceRule = recurrence?.Rule.ToRruleString(),
                    RecurrenceExDatesUtc = Array.Empty<DateTime>(),
                    RecurrenceEndUtcCached = recurrence?.EndUtcCached,
                    CreatedAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                };
            });
    }

    /// <summary>
    /// Shows the edit-event popover anchored to <paramref name="anchorElement"/>,
    /// pre-filled from <paramref name="eventToEdit"/>. Always edits the master
    /// row — callers must not pass an occurrence (see class summary).
    /// Existing EXDATEs are preserved across save; <c>RecurrenceEndUtcCached</c>
    /// is recomputed from the (possibly new) rule. <c>Id</c>, <c>Description</c>,
    /// <c>IsAllDay</c>, and <c>CreatedAtUtc</c> are preserved; <c>UpdatedAtUtc</c>
    /// is refreshed. Returns the edited <see cref="Event"/> on save, or
    /// <c>null</c> if dismissed.
    /// </summary>
    public static Task<Event?> ShowEditEventAsync(
        FrameworkElement anchorElement,
        Event eventToEdit,
        IList<Calendar> availableCalendars,
        FlyoutPlacementMode placement = FlyoutPlacementMode.RightEdgeAlignedTop)
    {
        if (eventToEdit.IsOccurrence)
        {
            throw new ArgumentException(
                "ShowEditEventAsync edits the master row; callers must load "
                + "the master and pass it (see EventRepository.GetByIdAsync). "
                + "Occurrence-scoped editing is deferred to Phase 2.",
                nameof(eventToEdit));
        }

        var initialRecurrence = TryParseRule(eventToEdit.RecurrenceRule);
        var preservedExDates = eventToEdit.RecurrenceExDatesUtc;

        return ShowAsync(
            anchorElement,
            placement,
            heading: "Edit Event",
            initialTitle: eventToEdit.Title,
            initialStartLocal: eventToEdit.StartTimeUtc.ToLocalTime(),
            initialEndLocal: eventToEdit.EndTimeUtc.ToLocalTime(),
            calendars: availableCalendars,
            selectedCalendarId: eventToEdit.CalendarId,
            initialRecurrence: initialRecurrence,
            showRecurringBanner: initialRecurrence is not null,
            buildEvent: (title, calendarId, startUtc, endUtc, recurrence) => new Event
            {
                Id = eventToEdit.Id,
                CalendarId = calendarId,
                Title = title,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                Description = eventToEdit.Description,
                IsAllDay = eventToEdit.IsAllDay,
                RecurrenceRule = recurrence?.Rule.ToRruleString(),
                // Preserve EXDATEs when the series remains recurring; clear
                // them if the user removed recurrence entirely (the projection
                // space they pointed into no longer exists).
                RecurrenceExDatesUtc = recurrence is null
                    ? Array.Empty<DateTime>()
                    : preservedExDates,
                RecurrenceEndUtcCached = recurrence?.EndUtcCached,
                CreatedAtUtc = eventToEdit.CreatedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow
            });
    }

    private static RecurrenceRule? TryParseRule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            return null;
        try { return RecurrenceRule.Parse(rrule); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Parsed recurrence values flowing from the form to <c>buildEvent</c>.
    /// <see cref="EndUtcCached"/> is null when the rule is infinite (no
    /// COUNT, no UNTIL); otherwise it is computed via
    /// <see cref="RecurrenceExpander.ComputeEndUtc"/> on save so the range
    /// query can prune ended finite series.
    /// </summary>
    private readonly record struct RecurrenceSelection(
        RecurrenceRule Rule,
        DateTime? EndUtcCached);

    // ── Shared implementation ─────────────────────────────────────────────

    private static Task<Event?> ShowAsync(
        FrameworkElement anchorElement,
        FlyoutPlacementMode placement,
        string heading,
        string initialTitle,
        DateTime initialStartLocal,
        DateTime initialEndLocal,
        IList<Calendar> calendars,
        Guid? selectedCalendarId,
        RecurrenceRule? initialRecurrence,
        bool showRecurringBanner,
        Func<string, Guid, DateTime, DateTime, RecurrenceSelection?, Event> buildEvent)
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
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 2)
        });

        if (showRecurringBanner)
            root.Children.Add(BuildRecurringBanner());

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

        // Repeats picker (frequency + optional weekly day chips + ends mode).
        var recurrencePicker = BuildRecurrencePicker(
            initialRecurrence, initialStartLocal);
        root.Children.Add(MakeLabel("Repeats"));
        root.Children.Add(recurrencePicker.Root);

        // Inline error (hidden until validation fails).
        var errorBlock = new TextBlock
        {
            Foreground = DangerBrush,
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

        var flyout = new Flyout
        {
            Content = root,
            Placement = placement
        };

        saveButton.Click += (s, e) =>
        {
            if (TryBuildEvent(
                    nameBox, calendarCombo, startDate, startTime, endDate, endTime,
                    recurrencePicker, buildEvent, errorBlock, out var result))
            {
                tcs.TrySetResult(result);
                flyout.Hide();
            }
        };

        cancelButton.Click += (s, e) =>
        {
            tcs.TrySetResult(null);
            flyout.Hide();
        };

        flyout.Closed += (s, e) => tcs.TrySetResult(null);
        flyout.ShowAt(anchorElement);

        return tcs.Task;
    }

    private static bool TryBuildEvent(
        TextBox nameBox,
        ComboBox calendarCombo,
        DatePicker startDate,
        TimePicker startTime,
        DatePicker endDate,
        TimePicker endTime,
        RecurrencePicker recurrencePicker,
        Func<string, Guid, DateTime, DateTime, RecurrenceSelection?, Event> buildEvent,
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

        if (!recurrencePicker.TryRead(startUtc, endUtc, out var recurrence, out var recurrenceError))
            return Fail(errorBlock, recurrenceError!);

        try
        {
            var evt = buildEvent(title, calendar.Id, startUtc, endUtc, recurrence);
            evt.Validate();
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

    // ── Recurring banner ──────────────────────────────────────────────────

    private static Border BuildRecurringBanner()
    {
        var text = new TextBlock
        {
            Text = "This event recurs — saving will update all occurrences "
                 + "in the series.",
            Foreground = Text2Brush,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        };

        return new Border
        {
            Background = ElevatedBrush,
            BorderBrush = HairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            Child = text
        };
    }

    // ── Recurrence picker ─────────────────────────────────────────────────

    private enum FrequencyChoice { None, Daily, Weekly, Monthly, Yearly }
    private enum EndsChoice { Never, OnDate, AfterN }

    /// <summary>
    /// Bag of controls + a reader closure for the recurrence picker. The
    /// reader is the only externally-visible interface; ShowAsync wires
    /// visibility, TryBuildEvent reads.
    /// </summary>
    private sealed class RecurrencePicker
    {
        public required StackPanel Root { get; init; }
        public required Func<DateTime, DateTime, (RecurrenceSelection? selection, string? error, bool ok)>
            ReadRaw { get; init; }

        public bool TryRead(
            DateTime startUtc, DateTime endUtc,
            out RecurrenceSelection? selection, out string? error)
        {
            var (sel, err, ok) = ReadRaw(startUtc, endUtc);
            selection = sel;
            error = err;
            return ok;
        }
    }

    private static RecurrencePicker BuildRecurrencePicker(
        RecurrenceRule? initial,
        DateTime initialStartLocal)
    {
        var root = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6 };

        // Frequency
        var freqCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Does not repeat", "Daily", "Weekly", "Monthly", "Yearly" }
        };
        root.Children.Add(freqCombo);

        // Weekly day chips
        var (chipsRow, dayChips) = BuildWeekdayChips();
        root.Children.Add(chipsRow);

        // Ends row
        var endsLabel = MakeLabel("Ends");
        endsLabel.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(endsLabel);

        var endsCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Never", "On date", "After N occurrences" }
        };
        root.Children.Add(endsCombo);

        var untilDate = new DatePicker
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Date = new DateTimeOffset(initialStartLocal.AddMonths(3))
        };
        root.Children.Add(untilDate);

        // TextBox (not NumberBox): NumberBox's template is significantly
        // heavier to instantiate and only earns its keep when spinner UX
        // matters. We validate as an int in ReadPicker and surface the
        // error inline like every other field.
        var countBox = new TextBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Text = "10",
            PlaceholderText = "Number of occurrences"
        };
        root.Children.Add(countBox);

        // Seed from initial rule (or "Does not repeat" by default).
        SeedFromRule(initial, initialStartLocal, freqCombo, dayChips, endsCombo, untilDate, countBox);

        // Visibility wiring
        void RefreshVisibility()
        {
            var freq = (FrequencyChoice)freqCombo.SelectedIndex;
            chipsRow.Visibility = freq == FrequencyChoice.Weekly
                ? Visibility.Visible : Visibility.Collapsed;
            var repeats = freq != FrequencyChoice.None;
            endsLabel.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            endsCombo.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;

            var ends = (EndsChoice)endsCombo.SelectedIndex;
            untilDate.Visibility = repeats && ends == EndsChoice.OnDate
                ? Visibility.Visible : Visibility.Collapsed;
            countBox.Visibility = repeats && ends == EndsChoice.AfterN
                ? Visibility.Visible : Visibility.Collapsed;
        }
        freqCombo.SelectionChanged += (s, e) => RefreshVisibility();
        endsCombo.SelectionChanged += (s, e) => RefreshVisibility();
        RefreshVisibility();

        return new RecurrencePicker
        {
            Root = root,
            ReadRaw = (startUtc, endUtc) =>
                ReadPicker(
                    freqCombo, dayChips, endsCombo, untilDate, countBox,
                    startUtc, endUtc)
        };
    }

    private static (StackPanel row, ToggleButton[] chips) BuildWeekdayChips()
    {
        // Sunday-first to match the rest of Chronicle (DateHelpers.BuildWeek).
        var labels = new[] { "S", "M", "T", "W", "T", "F", "S" };
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4
        };
        var chips = new ToggleButton[7];
        for (int i = 0; i < 7; i++)
        {
            chips[i] = new ToggleButton
            {
                Content = labels[i],
                MinWidth = 36,
                Padding = new Thickness(0)
            };
            row.Children.Add(chips[i]);
        }
        return (row, chips);
    }

    private static void SeedFromRule(
        RecurrenceRule? rule,
        DateTime initialStartLocal,
        ComboBox freqCombo,
        ToggleButton[] dayChips,
        ComboBox endsCombo,
        DatePicker untilDate,
        TextBox countBox)
    {
        if (rule is null)
        {
            freqCombo.SelectedIndex = (int)FrequencyChoice.None;
            // Default weekly chip to start-day's weekday so toggling to
            // Weekly produces a sensible rule immediately.
            dayChips[(int)initialStartLocal.DayOfWeek].IsChecked = true;
            endsCombo.SelectedIndex = (int)EndsChoice.Never;
            return;
        }

        freqCombo.SelectedIndex = rule.Frequency switch
        {
            RecurrenceFrequency.Daily   => (int)FrequencyChoice.Daily,
            RecurrenceFrequency.Weekly  => (int)FrequencyChoice.Weekly,
            RecurrenceFrequency.Monthly => (int)FrequencyChoice.Monthly,
            RecurrenceFrequency.Yearly  => (int)FrequencyChoice.Yearly,
            _ => (int)FrequencyChoice.None,
        };

        if (rule.ByDay != WeekdaySet.None)
        {
            for (int i = 0; i < 7; i++)
            {
                var dow = (DayOfWeek)i;
                dayChips[i].IsChecked =
                    (rule.ByDay & RecurrenceRule.FromDayOfWeek(dow)) != 0;
            }
        }
        else
        {
            dayChips[(int)initialStartLocal.DayOfWeek].IsChecked = true;
        }

        if (rule.UntilUtc is DateTime until)
        {
            endsCombo.SelectedIndex = (int)EndsChoice.OnDate;
            untilDate.Date = new DateTimeOffset(until.ToLocalTime());
        }
        else if (rule.Count is int count)
        {
            endsCombo.SelectedIndex = (int)EndsChoice.AfterN;
            countBox.Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            endsCombo.SelectedIndex = (int)EndsChoice.Never;
        }
    }

    private static (RecurrenceSelection? selection, string? error, bool ok) ReadPicker(
        ComboBox freqCombo,
        ToggleButton[] dayChips,
        ComboBox endsCombo,
        DatePicker untilDate,
        TextBox countBox,
        DateTime startUtc,
        DateTime endUtc)
    {
        var freq = (FrequencyChoice)freqCombo.SelectedIndex;
        if (freq == FrequencyChoice.None)
            return (null, null, true);

        RecurrenceRule rule;
        switch (freq)
        {
            case FrequencyChoice.Daily:
                rule = RecurrenceRule.Daily();
                break;

            case FrequencyChoice.Weekly:
                var days = WeekdaySet.None;
                for (int i = 0; i < 7; i++)
                {
                    if (dayChips[i].IsChecked == true)
                        days |= RecurrenceRule.FromDayOfWeek((DayOfWeek)i);
                }
                if (days == WeekdaySet.None)
                    return (null, "Pick at least one day of the week.", false);
                rule = RecurrenceRule.Weekly(days);
                break;

            case FrequencyChoice.Monthly:
                rule = RecurrenceRule.Monthly(byMonthDay: startUtc.ToLocalTime().Day);
                break;

            case FrequencyChoice.Yearly:
                rule = RecurrenceRule.Yearly();
                break;

            default:
                return (null, "Unsupported repeat option.", false);
        }

        var ends = (EndsChoice)endsCombo.SelectedIndex;
        switch (ends)
        {
            case EndsChoice.Never:
                break;

            case EndsChoice.OnDate:
                var until = DateHelpers.CombineLocalDateAndTimeAsUtc(
                    untilDate.Date.Date, TimeSpan.Zero).AddDays(1).AddTicks(-1);
                if (until < startUtc)
                    return (null, "End date must be on or after the start date.", false);
                rule = rule.WithUntil(until);
                break;

            case EndsChoice.AfterN:
                if (!int.TryParse(
                        countBox.Text,
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var count)
                    || count < 1)
                {
                    return (null, "Occurrence count must be a positive integer.", false);
                }
                rule = rule.WithCount(count);
                break;
        }

        var duration = endUtc - startUtc;
        var cachedEnd = RecurrenceExpander.ComputeEndUtc(startUtc, duration, rule);
        return (new RecurrenceSelection(rule, cachedEnd), null, true);
    }

    // ── Form building helpers ─────────────────────────────────────────────

    private static TextBlock MakeLabel(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Text2Brush,
        Margin = new Thickness(0, 2, 0, 0)
    };

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
