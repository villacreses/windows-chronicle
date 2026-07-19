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
/// Recurring-event semantics: <see cref="ShowEditEventAsync"/> edits the
/// master row (All-events scope) — callers load the master before invoking
/// it; this matches the persistence boundary enforced by
/// <c>EventRepository.RefuseOccurrence</c>. The Phase 2A
/// <see cref="ShowEditOccurrenceAsync"/> entry handles the
/// This-event-only scope — pre-fills from the occurrence's merged values,
/// hides Calendar / Repeats, and returns an <see cref="Event"/> the caller
/// converts to <c>OverrideFields</c> for the override write path.
///
/// The popover performs no persistence — it only constructs and returns the
/// <see cref="Event"/>; the caller saves it via the event repository.
/// </summary>
/// <summary>
/// What the master-path editor returns on save: the built <see cref="Event"/>
/// plus the reminder set the user chose. The popover builds both but persists
/// neither — <c>MainWindow</c> writes the event via <c>EventRepository</c> and
/// the reminders via <c>ReminderRepository.SetForEventAsync</c>. The reminder
/// list is 0..1 in the current editor (one "Remind me" dropdown), though the
/// domain and the write path both support a set of any size.
/// </summary>
public sealed record EventEditResult(Event Event, IReadOnlyList<Reminder> Reminders);

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
    /// Derives an IANA timezone id for the recurrence anchor frame of a
    /// newly-created recurring event, from the system local zone. Called only
    /// on the create path — edits preserve the master's existing TimeZoneId.
    /// The Windows→IANA normalization and its UTC fallback live in
    /// <see cref="RecurrenceTimeZone.NormalizeToIana"/>.
    /// </summary>
    private static string GetDefaultRecurringTimeZoneId()
        => RecurrenceTimeZone.NormalizeToIana(TimeZoneInfo.Local.Id);

    // "Remind me" picker: presets, seeding from an existing reminder set, and
    // resolving a selection back into the set to persist all live in the
    // pure Chronicle.Models.ReminderPickerModel (see .context/TESTING.md
    // Layer 5). This popover only wires the WinUI ComboBox to it. Shown only
    // on the master / standalone path — per-occurrence reminder overrides
    // are deferred, so an occurrence inherits the series reminders.

    /// <summary>
    /// Shows the create-event popover anchored to <paramref name="anchorElement"/>.
    /// Defaults: <paramref name="suggestedStartTime"/> for one hour, the first
    /// available calendar selected, does-not-repeat, no reminder. Returns the
    /// new <see cref="Event"/> plus chosen reminder set on save, or <c>null</c>
    /// if dismissed.
    /// </summary>
    public static Task<EventEditResult?> ShowCreateEventAsync(
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
            initialIsAllDay: false,
            initialDescription: null,
            existingReminders: Array.Empty<Reminder>(),
            calendars: availableCalendars,
            selectedCalendarId: availableCalendars.Count > 0 ? availableCalendars[0].Id : null,
            initialRecurrence: null,
            showRecurringBanner: false,
            buildEvent: (title, calendarId, startUtc, endUtc, isAllDay, description, recurrence) =>
            {
                var nowUtc = DateTime.UtcNow;
                // Phase 2B: TimeZoneId is resolved here (where we know
                // we're on the create path) and threaded into
                // ComputeEndUtc so the cached end is computed under the
                // same walk strategy the renderer will use.
                var tzId = recurrence is null
                    ? null
                    : GetDefaultRecurringTimeZoneId();
                var cachedEnd = recurrence is null
                    ? null
                    : RecurrenceExpander.ComputeEndUtc(
                        startUtc, endUtc - startUtc, recurrence, tzId);
                return new Event
                {
                    Id = Guid.NewGuid(),
                    CalendarId = calendarId,
                    Title = title,
                    StartTimeUtc = startUtc,
                    EndTimeUtc = endUtc,
                    Description = description,
                    IsAllDay = isAllDay,
                    RecurrenceRule = recurrence?.ToRruleString(),
                    RecurrenceExDatesUtc = Array.Empty<DateTime>(),
                    RecurrenceEndUtcCached = cachedEnd,
                    TimeZoneId = tzId,
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
    public static Task<EventEditResult?> ShowEditEventAsync(
        FrameworkElement anchorElement,
        Event eventToEdit,
        IReadOnlyList<Reminder> existingReminders,
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
            initialIsAllDay: eventToEdit.IsAllDay,
            initialDescription: eventToEdit.Description,
            existingReminders: existingReminders,
            calendars: availableCalendars,
            selectedCalendarId: eventToEdit.CalendarId,
            initialRecurrence: initialRecurrence,
            showRecurringBanner: initialRecurrence is not null,
            buildEvent: (title, calendarId, startUtc, endUtc, isAllDay, description, recurrence) =>
            {
                // Phase 2B TimeZoneId policy:
                //   - no recurrence on save → null (non-recurring).
                //   - newly added recurrence (eventToEdit had no rule) →
                //     default to the system zone; this is the "becoming
                //     recurring" path, treated as create-time anchoring.
                //   - already recurring → preserve. Could be null (legacy
                //     UTC-anchored series stay legacy — see DECISIONS.md,
                //     no auto-migration), or a real IANA zone.
                var tzId = recurrence is null
                    ? null
                    : eventToEdit.RecurrenceRule is null
                        ? GetDefaultRecurringTimeZoneId()
                        : eventToEdit.TimeZoneId;

                // EndUtcCached is computed here so it always matches the
                // walk strategy the renderer will use (tz-aware vs.
                // legacy UTC). Drift between the two paths would surface
                // as the range query incorrectly pruning a series.
                var cachedEnd = recurrence is null
                    ? null
                    : RecurrenceExpander.ComputeEndUtc(
                        startUtc, endUtc - startUtc, recurrence, tzId);

                return new Event
                {
                Id = eventToEdit.Id,
                CalendarId = calendarId,
                Title = title,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                Description = description,
                IsAllDay = isAllDay,
                RecurrenceRule = recurrence?.ToRruleString(),
                // Preserve EXDATEs when the series remains recurring; clear
                // them if the user removed recurrence entirely (the projection
                // space they pointed into no longer exists).
                RecurrenceExDatesUtc = recurrence is null
                    ? Array.Empty<DateTime>()
                    : preservedExDates,
                RecurrenceEndUtcCached = cachedEnd,
                TimeZoneId = tzId,
                CreatedAtUtc = eventToEdit.CreatedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow
                };
            });
    }

    /// <summary>
    /// Shows the occurrence-scoped edit popover for the This-event branch
    /// of the scope picker. Pre-fills from <paramref name="occurrence"/>'s
    /// merged values (which already reflect any prior override) and exposes
    /// a stripped form — no Calendar, no Repeats picker, no recurring banner.
    /// The recurrence rule is series-level; an occurrence-scoped edit cannot
    /// change it.
    ///
    /// Returns an <see cref="Event"/> carrying the edited values, or
    /// <c>null</c> if dismissed. The caller (<c>MainWindow</c>) converts the
    /// returned values into <c>OverrideFields</c> and writes via
    /// <c>OverrideRepository.UpsertAsync</c>; the returned Event itself is
    /// never persisted (a persistence-boundary attempt would be refused by
    /// <c>RefuseOccurrence</c>, since we attach <c>SeriesAnchorUtc</c>).
    /// </summary>
    public static Task<Event?> ShowEditOccurrenceAsync(
        FrameworkElement anchorElement,
        Event occurrence,
        FlyoutPlacementMode placement = FlyoutPlacementMode.RightEdgeAlignedTop)
    {
        if (!occurrence.IsOccurrence)
        {
            throw new ArgumentException(
                "ShowEditOccurrenceAsync requires an expanded occurrence "
                + "(SeriesAnchorUtc set). Use ShowEditEventAsync for masters "
                + "and standalones.",
                nameof(occurrence));
        }

        var tcs = new TaskCompletionSource<Event?>();

        var root = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Width = FormWidth,
            Spacing = 10
        };

        root.Children.Add(new TextBlock
        {
            Text = "Edit this occurrence",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 2)
        });

        // Name
        var nameBox = new TextBox
        {
            PlaceholderText = "Event title",
            Text = occurrence.Title,
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(MakeLabel("Name"));
        root.Children.Add(nameBox);

        // All-day toggle
        var allDayToggle = new ToggleSwitch
        {
            IsOn = occurrence.IsAllDay,
            OnContent = "All day",
            OffContent = "All day",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(allDayToggle);

        // Start
        var initialStartLocal = occurrence.StartTimeUtc.ToLocalTime();
        var initialEndLocal = occurrence.EndTimeUtc.ToLocalTime();

        var startDate = new DatePicker { Date = new DateTimeOffset(initialStartLocal) };
        var startTime = new TimePicker { Time = initialStartLocal.TimeOfDay };
        root.Children.Add(MakeLabel("Start"));
        root.Children.Add(MakeDateTimeRow(startDate, startTime));

        // End
        var endDate = new DatePicker { Date = new DateTimeOffset(initialEndLocal) };
        var endTime = new TimePicker { Time = initialEndLocal.TimeOfDay };
        root.Children.Add(MakeLabel("End"));
        root.Children.Add(MakeDateTimeRow(endDate, endTime));

        void ApplyAllDayVisibility()
        {
            var allDay = allDayToggle.IsOn;
            startTime.IsEnabled = !allDay;
            endTime.IsEnabled = !allDay;
        }
        allDayToggle.Toggled += (s, e) => ApplyAllDayVisibility();
        ApplyAllDayVisibility();

        // Notes (multi-line description)
        var notesBox = new TextBox
        {
            PlaceholderText = "Notes",
            Text = occurrence.Description ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64
        };
        root.Children.Add(MakeLabel("Notes"));
        root.Children.Add(notesBox);

        var errorBlock = new TextBlock
        {
            Foreground = DangerBrush,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        root.Children.Add(errorBlock);

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
            var title = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                Fail(errorBlock, "Event name is required.");
                return;
            }

            var isAllDay = allDayToggle.IsOn;

            DateTime startUtc;
            DateTime endUtc;
            if (isAllDay)
            {
                // Phase A constraint: all-day is single-day. See the
                // matching comment in ShowAsync / TryBuildEvent.
                if (endDate.Date.Date != startDate.Date.Date)
                {
                    Fail(errorBlock, "All-day events must start and end on the same day.");
                    return;
                }

                startUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(
                    startDate.Date.Date, TimeSpan.Zero);
                endUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(
                    startDate.Date.Date.AddDays(1), TimeSpan.Zero);
            }
            else
            {
                startUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(
                    startDate.Date.Date, startTime.Time);
                endUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(
                    endDate.Date.Date, endTime.Time);

                if (startUtc >= endUtc)
                {
                    Fail(errorBlock, "End time must be after start time.");
                    return;
                }
            }

            var description = notesBox.Text?.Trim();
            if (string.IsNullOrEmpty(description))
                description = null;

            // The returned Event carries the edited values plus
            // SeriesAnchorUtc + Id so the caller can route the write
            // (Id = master id by the identity contract; SeriesAnchorUtc =
            // the anchor of this occurrence). Description / IsAllDay are
            // now editable and flow into OverrideFields at the caller;
            // Calendar and recurrence fields remain series-level.
            var result = new Event
            {
                Id = occurrence.Id,
                CalendarId = occurrence.CalendarId,
                Title = title,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                Description = description,
                IsAllDay = isAllDay,
                RecurrenceRule = null,
                RecurrenceExDatesUtc = Array.Empty<DateTime>(),
                RecurrenceEndUtcCached = null,
                SeriesAnchorUtc = occurrence.SeriesAnchorUtc,
                CreatedAtUtc = occurrence.CreatedAtUtc,
                UpdatedAtUtc = DateTime.UtcNow
            };

            tcs.TrySetResult(result);
            flyout.Hide();
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

    private static RecurrenceRule? TryParseRule(string? rrule)
    {
        if (string.IsNullOrWhiteSpace(rrule))
            return null;
        try { return RecurrenceRule.Parse(rrule); }
        catch (FormatException) { return null; }
    }

    // RecurrenceSelection (a wrapper over (Rule, EndUtcCached)) was
    // removed in Phase 2B sub-step 2: EndUtcCached now depends on
    // TimeZoneId, which is only known inside buildEvent. The picker
    // returns just the rule; buildEvent computes the cached end via
    // ComputeEndUtc with the appropriate TimeZoneId.

    // ── Shared implementation ─────────────────────────────────────────────

    private static Task<EventEditResult?> ShowAsync(
        FrameworkElement anchorElement,
        FlyoutPlacementMode placement,
        string heading,
        string initialTitle,
        DateTime initialStartLocal,
        DateTime initialEndLocal,
        bool initialIsAllDay,
        string? initialDescription,
        IReadOnlyList<Reminder> existingReminders,
        IList<Calendar> calendars,
        Guid? selectedCalendarId,
        RecurrenceRule? initialRecurrence,
        bool showRecurringBanner,
        Func<string, Guid, DateTime, DateTime, bool, string?, RecurrenceRule?, Event> buildEvent)
    {
        var tcs = new TaskCompletionSource<EventEditResult?>();

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

        // All-day toggle
        var allDayToggle = new ToggleSwitch
        {
            IsOn = initialIsAllDay,
            OnContent = "All day",
            OffContent = "All day",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        root.Children.Add(allDayToggle);

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

        // TimePickers dim when all-day is on; the pickers' state is ignored
        // by TryBuildEvent in that mode (start/end come from the dates plus
        // midnight), so disabling them makes the invalid state unreachable
        // rather than merely visually suppressed.
        void ApplyAllDayVisibility()
        {
            var allDay = allDayToggle.IsOn;
            startTime.IsEnabled = !allDay;
            endTime.IsEnabled = !allDay;
        }
        allDayToggle.Toggled += (s, e) => ApplyAllDayVisibility();
        ApplyAllDayVisibility();

        // Repeats picker (frequency + optional weekly day chips + ends mode).
        var recurrencePicker = BuildRecurrencePicker(
            initialRecurrence, initialStartLocal);
        root.Children.Add(MakeLabel("Repeats"));
        root.Children.Add(recurrencePicker.Root);

        // Notes (multi-line description). Placed after Repeats so the
        // primary scheduling fields stay grouped and the free-form field
        // ends up at the bottom where it can grow without pushing the
        // Save/Cancel row out of view.
        var notesBox = new TextBox
        {
            PlaceholderText = "Notes",
            Text = initialDescription ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64
        };
        root.Children.Add(MakeLabel("Notes"));
        root.Children.Add(notesBox);

        // Remind me (single reminder; master/standalone path only). Presets,
        // seeding, and the preserve-vs-replace save logic live in
        // ReminderPickerModel.
        var reminderCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = ReminderPickerModel.BuildLabels(existingReminders),
            SelectedIndex = ReminderPickerModel.SeedIndex(existingReminders)
        };
        root.Children.Add(MakeLabel("Remind me"));
        root.Children.Add(reminderCombo);

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
                    nameBox, calendarCombo, allDayToggle,
                    startDate, startTime, endDate, endTime,
                    notesBox, reminderCombo, existingReminders,
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
        ToggleSwitch allDayToggle,
        DatePicker startDate,
        TimePicker startTime,
        DatePicker endDate,
        TimePicker endTime,
        TextBox notesBox,
        ComboBox reminderCombo,
        IReadOnlyList<Reminder> existingReminders,
        RecurrencePicker recurrencePicker,
        Func<string, Guid, DateTime, DateTime, bool, string?, RecurrenceRule?, Event> buildEvent,
        TextBlock errorBlock,
        out EventEditResult? result)
    {
        result = null;

        var title = nameBox.Text?.Trim();
        if (string.IsNullOrEmpty(title))
            return Fail(errorBlock, "Event name is required.");

        if (calendarCombo.SelectedItem is not Calendar calendar)
            return Fail(errorBlock, "Please select a calendar.");

        var isAllDay = allDayToggle.IsOn;

        // All-day is bounded by local midnight on both sides; the time
        // pickers are ignored (and disabled) in that mode. End is the
        // start of the day *after* the end date so single-day all-day
        // events span [D 00:00, D+1 00:00).
        DateTime startUtc;
        DateTime endUtc;
        if (isAllDay)
        {
            // Phase A constraint: all-day events are single-day. Multi-day
            // all-day would render only on the start day (GroupVisibleByDay
            // keys by StartTimeUtc's local date), and fixing that fan-out
            // is entangled with multi-day spanning bars — both deferred to
            // the design overhaul (see BACKLOG.md).
            if (endDate.Date.Date != startDate.Date.Date)
                return Fail(errorBlock, "All-day events must start and end on the same day.");

            startUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(startDate.Date.Date, TimeSpan.Zero);
            endUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(startDate.Date.Date.AddDays(1), TimeSpan.Zero);
        }
        else
        {
            startUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(startDate.Date.Date, startTime.Time);
            endUtc = DateHelpers.CombineLocalDateAndTimeAsUtc(endDate.Date.Date, endTime.Time);

            if (startUtc >= endUtc)
                return Fail(errorBlock, "End time must be after start time.");
        }

        if (!recurrencePicker.TryRead(startUtc, endUtc, out var recurrence, out var recurrenceError))
            return Fail(errorBlock, recurrenceError!);

        // Trim + null-empty so a blank Notes box round-trips as NULL in
        // storage rather than the empty string (matches the pre-Phase-A
        // shape of the column).
        var description = notesBox.Text?.Trim();
        if (string.IsNullOrEmpty(description))
            description = null;

        try
        {
            var evt = buildEvent(title, calendar.Id, startUtc, endUtc, isAllDay, description, recurrence);
            evt.Validate();
            var reminders = ReminderPickerModel.ResolveForSave(
                existingReminders, reminderCombo.SelectedIndex, evt.Id);
            result = new EventEditResult(evt, reminders);
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
    //
    // Frequency/ends choices, the picker-state record, and the rule ⇄ state
    // mapping live in Chronicle.Models.Recurrence.RecurrencePickerModel; this
    // section owns only the WinUI controls and their wiring.

    /// <summary>
    /// Bag of controls + a reader closure for the recurrence picker. The
    /// reader is the only externally-visible interface; ShowAsync wires
    /// visibility, TryBuildEvent reads.
    /// </summary>
    private sealed class RecurrencePicker
    {
        public required StackPanel Root { get; init; }
        public required Func<DateTime, DateTime, (RecurrenceRule? rule, string? error, bool ok)>
            ReadRaw { get; init; }

        public bool TryRead(
            DateTime startUtc, DateTime endUtc,
            out RecurrenceRule? rule, out string? error)
        {
            var (r, err, ok) = ReadRaw(startUtc, endUtc);
            rule = r;
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
            var freq = (RecurrenceFrequencyChoice)freqCombo.SelectedIndex;
            chipsRow.Visibility = freq == RecurrenceFrequencyChoice.Weekly
                ? Visibility.Visible : Visibility.Collapsed;
            var repeats = freq != RecurrenceFrequencyChoice.None;
            endsLabel.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;
            endsCombo.Visibility = repeats ? Visibility.Visible : Visibility.Collapsed;

            var ends = (RecurrenceEndChoice)endsCombo.SelectedIndex;
            untilDate.Visibility = repeats && ends == RecurrenceEndChoice.OnDate
                ? Visibility.Visible : Visibility.Collapsed;
            countBox.Visibility = repeats && ends == RecurrenceEndChoice.AfterN
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
        var state = RecurrencePickerModel.SeedState(rule, initialStartLocal);

        freqCombo.SelectedIndex = (int)state.Frequency;
        for (int i = 0; i < 7; i++)
            dayChips[i].IsChecked =
                (state.WeeklyDays & RecurrenceRule.FromDayOfWeek((DayOfWeek)i)) != 0;
        endsCombo.SelectedIndex = (int)state.End;
        untilDate.Date = new DateTimeOffset(state.UntilLocalDate);
        countBox.Text = state.CountText;
    }

    private static (RecurrenceRule? rule, string? error, bool ok) ReadPicker(
        ComboBox freqCombo,
        ToggleButton[] dayChips,
        ComboBox endsCombo,
        DatePicker untilDate,
        TextBox countBox,
        DateTime startUtc,
        DateTime endUtc)
    {
        var days = WeekdaySet.None;
        for (int i = 0; i < 7; i++)
        {
            if (dayChips[i].IsChecked == true)
                days |= RecurrenceRule.FromDayOfWeek((DayOfWeek)i);
        }

        var state = new RecurrencePickerState(
            (RecurrenceFrequencyChoice)freqCombo.SelectedIndex,
            days,
            (RecurrenceEndChoice)endsCombo.SelectedIndex,
            untilDate.Date.Date,
            countBox.Text);

        // EndUtcCached depends on TimeZoneId, which only buildEvent knows, so
        // the picker returns just the rule; the caller computes the cached end
        // via RecurrenceExpander.ComputeEndUtc with the appropriate tz.
        return RecurrencePickerModel.BuildRule(state, startUtc);
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
