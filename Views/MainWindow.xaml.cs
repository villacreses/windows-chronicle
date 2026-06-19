using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Views.Dialogs;
using Chronicle.Views.Popovers;
using Chronicle.Views.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Chronicle
{
    /// <summary>The main content view. Pure UI mode — not navigation state.</summary>
    internal enum CalendarView { Month, Week, Day }

    public sealed partial class MainWindow : Window, ICalendarInteractionHost, ISidebarHost
    {
        private readonly EventRepository _eventRepository = new();
        private readonly CalendarRepository _calendarRepository = new();

        private readonly SidebarRenderer _sidebarRenderer;
        private readonly CalendarGridRenderer _calendarGridRenderer;
        private readonly MiniMonthRenderer _miniMonthRenderer;
        private readonly SelectedDayRenderer _selectedDayRenderer;
        private readonly WeekViewRenderer _weekViewRenderer;
        private readonly DayViewRenderer _dayViewRenderer;
        private readonly CalendarDialogService _calendarDialogService;

        private readonly EventPopover _eventPopover;
        private readonly Flyout _eventPopoverFlyout;

        // Navigation state — single source of truth.
        //   _displayMonth: first day of the month currently shown by both grids.
        //   _selectedDate: the user's focused day (defaults to today). Kept
        //                  separate from _displayMonth so future Week/Day/Agenda
        //                  views can build on a stable "focused date" concept.
        private DateTime _displayMonth;
        private DateTime _selectedDate;

        // Active content view. Week View derives its week from _selectedDate,
        // so this is a display mode, not a separate piece of date state.
        private CalendarView _currentView = CalendarView.Month;

        // Calendars are loaded once at startup and cached for sidebar rendering.
        private List<Calendar> _allCalendars = new();

        // In-memory visibility state: CalendarId → visible.
        // Seeded to true for every calendar; resets on app restart (by design).
        private Dictionary<Guid, bool> _calendarVisibility = new();

        // Raw events for the currently loaded range, before the visibility
        // filter. _eventsByDate is derived from this plus _calendarVisibility;
        // visibility toggles re-filter without going to the DB. The loaded
        // range tracks what the cache covers so EnsureEventsLoadedAsync can
        // skip the query when the active view's range fits inside it (e.g.,
        // switching Month → Week → Day in place).
        private List<Event> _loadedEvents = new();
        private DateTime _loadedRangeStartUtc = DateTime.MaxValue;
        private DateTime _loadedRangeEndUtc = DateTime.MinValue;

        private Dictionary<DateTime, List<Event>> _eventsByDate = new();

        public MainWindow()
        {
            InitializeComponent();

            var now = DateTime.Now;
            _displayMonth = new DateTime(now.Year, now.Month, 1);
            _selectedDate = DateHelpers.GetLocalDayKey(now);

            _sidebarRenderer = new SidebarRenderer(SidebarPanel, this);
            _calendarGridRenderer = new CalendarGridRenderer(DayNamesGrid, CalendarGrid, this);
            _miniMonthRenderer = new MiniMonthRenderer(MiniMonthPanel, this);
            _selectedDayRenderer = new SelectedDayRenderer(SelectedDayPanel, this);
            _weekViewRenderer = new WeekViewRenderer(WeekViewRoot, this);
            _dayViewRenderer = new DayViewRenderer(DayViewRoot, this);
            _calendarDialogService = new CalendarDialogService(
                _calendarRepository, _eventRepository, () => Content.XamlRoot, ReloadCalendarsAndRefreshAsync);

            _eventPopover = new EventPopover();
            _eventPopoverFlyout = new Flyout
            {
                Content = _eventPopover,
                Placement = FlyoutPlacementMode.RightEdgeAlignedTop
            };

            _eventPopover.EditRequested += EventPopover_EditRequested;
            _eventPopover.DeleteRequested += EventPopover_DeleteRequested;
            _eventPopover.CloseRequested += (s, e) => _eventPopoverFlyout.Hide();

            PrevMonthButton.Click += PrevMonthButton_Click;
            NextMonthButton.Click += NextMonthButton_Click;
            TodayButton.Click += TodayButton_Click;

            MonthViewToggle.Click += (s, e) => SwitchView(CalendarView.Month);
            WeekViewToggle.Click += (s, e) => SwitchView(CalendarView.Week);
            DayViewToggle.Click += (s, e) => SwitchView(CalendarView.Day);

            DispatcherQueue.TryEnqueue(async () => await InitializeCalendarAsync());
        }

        // ── Initialization ────────────────────────────────────────────────────

        private async Task InitializeCalendarAsync()
        {
            try
            {
                // Load calendars + sidebar first so visibility state is ready
                // before RefreshActiveViewAsync filters events.
                await ReloadCalendarsAndRefreshAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error initializing calendar: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Reloads calendars from storage, reconciles the visibility map
        /// (new calendars default to visible; deleted ones are dropped),
        /// re-renders the sidebar, and refreshes the active view. Used at
        /// startup and after any create/edit/delete calendar operation.
        ///
        /// Invalidates the event cache because calendar delete cascade-deletes
        /// events (see DECISIONS.md). Create/edit don't change events but the
        /// extra query is cheap and not worth special-casing.
        /// </summary>
        private async Task ReloadCalendarsAndRefreshAsync()
        {
            _allCalendars = await _calendarRepository.GetAllAsync();

            foreach (var cal in _allCalendars)
                _calendarVisibility.TryAdd(cal.Id, true);

            var existingIds = _allCalendars.Select(c => c.Id).ToHashSet();
            foreach (var staleId in _calendarVisibility.Keys.Where(id => !existingIds.Contains(id)).ToList())
                _calendarVisibility.Remove(staleId);

            RenderSidebar();
            InvalidateLoadedEvents();
            await RefreshActiveViewAsync();
        }

        // ── Full refresh (single re-render entry point) ───────────────────────

        /// <summary>
        /// Reloads events for the active view's date range and re-renders the
        /// active main view (Month or Week) plus the always-present sidebar
        /// pieces (mini month, selected-day panel) and the period header.
        /// </summary>
        private async Task RefreshActiveViewAsync()
        {
            await LoadEventsAsync();

            switch (_currentView)
            {
                case CalendarView.Month:
                    _calendarGridRenderer.RenderDayHeaders();
                    RenderCalendarGrid();
                    break;
                case CalendarView.Week:
                    RenderWeekView();
                    break;
                case CalendarView.Day:
                    RenderDayView();
                    break;
            }

            RenderMiniMonth();
            RenderSelectedDay();
            UpdateHeader();
        }

        private void UpdateHeader()
        {
            MonthYearText.Text = _currentView switch
            {
                CalendarView.Week => FormatWeekRange(_selectedDate),
                CalendarView.Day => _selectedDate.ToString("dddd, MMMM d, yyyy"),
                _ => _displayMonth.ToString("MMMM yyyy")
            };
        }

        private static string FormatWeekRange(DateTime dateInWeek)
        {
            var week = DateHelpers.BuildWeek(dateInWeek);
            var start = week[0];
            var end = week[6];

            // "Jun 14 – 20, 2026" within one month; otherwise spell both months
            // (and both years across a year boundary).
            if (start.Year != end.Year)
                return $"{start:MMM d, yyyy} – {end:MMM d, yyyy}";
            if (start.Month != end.Month)
                return $"{start:MMM d} – {end:MMM d}, {end:yyyy}";
            return $"{start:MMM d} – {end:d}, {end:yyyy}";
        }

        // ── Navigation handlers ───────────────────────────────────────────────

        private async void PrevMonthButton_Click(object sender, RoutedEventArgs e)
        {
            StepPeriod(-1);
            await RefreshActiveViewAsync();
        }

        private async void NextMonthButton_Click(object sender, RoutedEventArgs e)
        {
            StepPeriod(1);
            await RefreshActiveViewAsync();
        }

        /// <summary>
        /// Moves Previous/Next by one period of the active view: a month in
        /// Month view, a week in Week view, a single day in Day view.
        /// </summary>
        private void StepPeriod(int direction)
        {
            switch (_currentView)
            {
                case CalendarView.Week:
                    StepWeek(direction);
                    break;
                case CalendarView.Day:
                    StepDay(direction);
                    break;
                default:
                    _displayMonth = _displayMonth.AddMonths(direction);
                    break;
            }
        }

        /// <summary>
        /// Moves the visible day by <paramref name="direction"/> days. Day View
        /// derives entirely from <see cref="_selectedDate"/>, so paging the day
        /// is just moving the selected day; the displayed month follows so the
        /// mini-month and month range stay in sync.
        /// </summary>
        private void StepDay(int direction)
        {
            _selectedDate = DateHelpers.GetLocalDayKey(_selectedDate.AddDays(direction));
            _displayMonth = DateHelpers.GetMonthStartLocal(_selectedDate);
        }

        private async void TodayButton_Click(object sender, RoutedEventArgs e)
        {
            var now = DateTime.Now;
            _displayMonth = new DateTime(now.Year, now.Month, 1);
            _selectedDate = DateHelpers.GetLocalDayKey(now);
            await RefreshActiveViewAsync();
        }

        /// <summary>
        /// Moves the visible week by <paramref name="direction"/> weeks. Because
        /// the week is derived from <see cref="_selectedDate"/>, paging the week
        /// is the same operation as moving the selected day by seven days; the
        /// displayed month follows so the mini-month and header stay in sync.
        /// </summary>
        private void StepWeek(int direction)
        {
            _selectedDate = DateHelpers.GetLocalDayKey(_selectedDate.AddDays(7 * direction));
            _displayMonth = DateHelpers.GetMonthStartLocal(_selectedDate);
        }

        // ── View switching ────────────────────────────────────────────────────

        private async void SwitchView(CalendarView view)
        {
            if (_currentView == view)
            {
                // Keep the toggles consistent if the user clicks the active one.
                UpdateViewToggles();
                return;
            }

            _currentView = view;
            UpdateViewToggles();

            MonthViewRoot.Visibility =
                view == CalendarView.Month ? Visibility.Visible : Visibility.Collapsed;
            WeekViewRoot.Visibility =
                view == CalendarView.Week ? Visibility.Visible : Visibility.Collapsed;
            DayViewRoot.Visibility =
                view == CalendarView.Day ? Visibility.Visible : Visibility.Collapsed;

            await RefreshActiveViewAsync();
        }

        private void UpdateViewToggles()
        {
            MonthViewToggle.IsChecked = _currentView == CalendarView.Month;
            WeekViewToggle.IsChecked = _currentView == CalendarView.Week;
            DayViewToggle.IsChecked = _currentView == CalendarView.Day;
        }

        // ── Sidebar ───────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the Calendars section in the sidebar from <see cref="_allCalendars"/>.
        /// </summary>
        private void RenderSidebar()
        {
            _sidebarRenderer.Render(_allCalendars, _calendarVisibility);
        }

        // ── ISidebarHost ──────────────────────────────────────────────────────

        public void OnCalendarVisibilityToggled(Guid calendarId, bool isVisible)
        {
            _calendarVisibility[calendarId] = isVisible;
            // Visibility is a client-side filter over the already-loaded events.
            // Re-filter and re-render — never hit the DB for a checkbox click.
            ApplyVisibilityFilter();
            RerenderActiveView();
        }

        public async void OnAddCalendar()
        {
            await _calendarDialogService.ShowCreateCalendarDialogAsync();
        }

        public async void OnEditCalendar(Calendar calendar)
        {
            await _calendarDialogService.ShowEditCalendarDialogAsync(calendar);
        }

        public async void OnDeleteCalendar(Calendar calendar)
        {
            await _calendarDialogService.ShowDeleteCalendarDialogAsync(calendar);
        }

        // ── Mini month navigator ──────────────────────────────────────────────

        private void RenderMiniMonth()
        {
            _miniMonthRenderer.Render(_displayMonth, _selectedDate);
        }

        // ── ICalendarInteractionHost: mini-month routing ──────────────────────

        /// <summary>
        /// A day was clicked in the mini month. Update the selected date and,
        /// if the clicked day belongs to a different month (e.g. a trailing/
        /// leading adjacent-month day), move the displayed month to match.
        /// </summary>
        public async void OnMiniMonthDateSelected(DateTime date)
        {
            if (!DateHelpers.IsInMonth(date, _displayMonth))
            {
                _selectedDate = DateHelpers.GetLocalDayKey(date);
                _displayMonth = new DateTime(date.Year, date.Month, 1);
                await RefreshActiveViewAsync();
            }
            else
            {
                SelectDate(date);
            }
        }

        public async void OnMiniMonthPrevMonth()
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            await RefreshActiveViewAsync();
        }

        public async void OnMiniMonthNextMonth()
        {
            _displayMonth = _displayMonth.AddMonths(1);
            await RefreshActiveViewAsync();
        }

        // ── Selected day ──────────────────────────────────────────────────────

        // ── ICalendarInteractionHost: day selection ───────────────────────────

        /// <summary>
        /// Selects a day, updating the shared <see cref="_selectedDate"/> and the
        /// views that depend on it. The fast path updates selection highlights
        /// incrementally without reloading events (the day is already loaded).
        ///
        /// In Week View, selecting a day in a different week changes which seven
        /// days are visible and may cross the loaded range, so that case falls
        /// back to a full refresh.
        /// </summary>
        public void OnDaySelected(DateTime date) => SelectDate(date);

        private async void SelectDate(DateTime date)
        {
            var previousDate = _selectedDate;
            _selectedDate = DateHelpers.GetLocalDayKey(date);

            // Some views' loaded ranges don't cover the new day, so they need a
            // full refresh: Week View when the week changes, Day View whenever
            // the day changes (its range is a single day).
            bool crossesLoadedRange =
                (_currentView == CalendarView.Week && !DateHelpers.IsSameWeek(previousDate, _selectedDate))
                || (_currentView == CalendarView.Day && !DateHelpers.IsSameDay(previousDate, _selectedDate));

            if (crossesLoadedRange)
            {
                _displayMonth = DateHelpers.GetMonthStartLocal(_selectedDate);
                await RefreshActiveViewAsync();
                return;
            }

            _miniMonthRenderer.UpdateSelectedDate(previousDate, _selectedDate);
            _calendarGridRenderer.UpdateSelectedDate(previousDate, _selectedDate);
            if (_currentView == CalendarView.Week)
                _weekViewRenderer.UpdateSelectedDate(previousDate, _selectedDate);
            RenderSelectedDay();
        }

        private void RenderSelectedDay()
        {
            var dayKey = DateHelpers.GetLocalDayKey(_selectedDate);
            var events = _eventsByDate.GetValueOrDefault(dayKey) ?? new List<Event>();
            _selectedDayRenderer.Render(_selectedDate, events, _allCalendars);
        }

        // ── Event create / edit popovers ──────────────────────────────────────

        /// <summary>
        /// Window-center anchor for the event popovers. Positions are relative to
        /// the window content. A precise per-interaction anchor can replace this
        /// later; centering is good enough and keeps callbacks simple.
        /// </summary>
        private Point WindowCenterAnchor()
            => Content is FrameworkElement root
                ? new Point(root.ActualWidth / 2, root.ActualHeight / 2)
                : new Point(0, 0);

        /// <summary>
        /// Opens the create-event popover for <paramref name="dayDate"/> at
        /// <paramref name="suggestedStartLocal"/>; on save, inserts the event and
        /// refreshes the active view. No-op if dismissed or no calendar exists.
        ///
        /// Shows a transient "(No title)" draft chip in the active view for the
        /// duration of the popover so the user has immediate visual feedback at
        /// the tap location. The draft is removed in <c>finally</c> — never
        /// persisted — and the view either refreshes from the DB (on save) or
        /// re-renders without the draft (on cancel/dismiss).
        /// </summary>
        private async Task CreateEventAsync(DateTime dayDate, DateTime suggestedStartLocal)
        {
            if (_allCalendars.Count == 0)
                return;

            var draft = BuildDraftEvent(suggestedStartLocal);
            InsertDraft(draft);
            RerenderActiveView();

            Event? created;
            try
            {
                created = await EventEditPopover.ShowCreateEventAsync(
                    this, WindowCenterAnchor(), suggestedStartLocal, _allCalendars);
            }
            finally
            {
                RemoveDraft(draft);
            }

            if (created is not null)
            {
                await _eventRepository.InsertAsync(created);
                InvalidateLoadedEvents();
                await RefreshActiveViewAsync();
            }
            else
            {
                RerenderActiveView();
            }
        }

        // ── Draft event (transient placeholder while the popover is open) ─────

        private const string DraftEventTitle = "(No title)";

        /// <summary>
        /// Builds a one-hour transient placeholder event starting at
        /// <paramref name="startLocal"/>, owned by the first available calendar
        /// (matching the popover's default). Never saved to the repository.
        /// </summary>
        private Event BuildDraftEvent(DateTime startLocal)
        {
            var startUtc = DateTime.SpecifyKind(startLocal, DateTimeKind.Local).ToUniversalTime();
            var nowUtc = DateTime.UtcNow;
            return new Event
            {
                Id = Guid.NewGuid(),
                CalendarId = _allCalendars[0].Id,
                Title = DraftEventTitle,
                StartTimeUtc = startUtc,
                EndTimeUtc = startUtc.AddHours(1),
                IsAllDay = false,
                Description = null,
                RecurrenceRuleJson = null,
                CreatedAtUtc = nowUtc,
                UpdatedAtUtc = nowUtc
            };
        }

        /// <summary>
        /// Inserts the draft into <see cref="_eventsByDate"/> at the front of its
        /// day's list, so Month View renders it at the top of the cell. Week and
        /// Day Views position by the draft's start time, which lands the chip at
        /// the tap location.
        /// </summary>
        private void InsertDraft(Event draft)
        {
            var key = DateHelpers.GetEventDayKey(draft.StartTimeUtc);
            if (!_eventsByDate.TryGetValue(key, out var list))
            {
                list = new List<Event>();
                _eventsByDate[key] = list;
            }
            list.Insert(0, draft);
        }

        private void RemoveDraft(Event draft)
        {
            var key = DateHelpers.GetEventDayKey(draft.StartTimeUtc);
            if (_eventsByDate.TryGetValue(key, out var list))
            {
                list.Remove(draft);
                if (list.Count == 0)
                    _eventsByDate.Remove(key);
            }
        }

        /// <summary>
        /// Re-renders just the active main view and the selected-day panel from
        /// the current <see cref="_eventsByDate"/> (no DB reload). Used to show
        /// and then hide the draft chip without round-tripping the repository.
        /// </summary>
        private void RerenderActiveView()
        {
            switch (_currentView)
            {
                case CalendarView.Month:
                    RenderCalendarGrid();
                    break;
                case CalendarView.Week:
                    RenderWeekView();
                    break;
                case CalendarView.Day:
                    RenderDayView();
                    break;
            }
            RenderSelectedDay();
        }

        /// <summary>
        /// Opens the edit-event popover for <paramref name="evt"/>; on save,
        /// updates the event and refreshes the active view. No-op if dismissed.
        /// </summary>
        private async Task EditEventAsync(Event evt)
        {
            var edited = await EventEditPopover.ShowEditEventAsync(
                this, WindowCenterAnchor(), evt, _allCalendars);
            if (edited is null)
                return;

            await _eventRepository.UpdateAsync(edited);
            InvalidateLoadedEvents();
            await RefreshActiveViewAsync();
        }

        // ── Event loading ─────────────────────────────────────────────────────

        /// <summary>
        /// The UTC range the active view needs to cover. Week/Day derive their
        /// range from <see cref="_selectedDate"/> (and can straddle a month
        /// boundary); Month uses <see cref="_displayMonth"/>.
        /// </summary>
        private (DateTime startUtc, DateTime endUtc) GetActiveViewRangeUtc() =>
            _currentView switch
            {
                CalendarView.Week => DateHelpers.GetWeekRangeUtc(_selectedDate),
                CalendarView.Day => DateHelpers.GetDayRangeUtc(_selectedDate),
                _ => DateHelpers.GetMonthRangeUtc(_displayMonth)
            };

        /// <summary>
        /// Full event-pipeline pass: query the DB if the cache doesn't cover
        /// the active view's range, then re-apply the visibility filter into
        /// <see cref="_eventsByDate"/>. Use after any change that invalidates
        /// the cache (navigation across the loaded range, event CRUD, calendar
        /// delete). UI-only changes (view switch, visibility toggle) should
        /// call the narrower helpers directly so they don't touch the DB.
        /// </summary>
        private async Task LoadEventsAsync()
        {
            await EnsureEventsLoadedAsync();
            ApplyVisibilityFilter();
        }

        /// <summary>
        /// Queries the DB for the active view's range only when the existing
        /// cache doesn't already cover it. View switches that stay inside the
        /// loaded range (Month → Week, Week → Day, etc.) short-circuit.
        /// </summary>
        private async Task EnsureEventsLoadedAsync()
        {
            var (rangeStart, rangeEnd) = GetActiveViewRangeUtc();

            if (rangeStart >= _loadedRangeStartUtc && rangeEnd <= _loadedRangeEndUtc)
                return;

            _loadedEvents = await _eventRepository.GetInRangeAsync(rangeStart, rangeEnd);
            _loadedRangeStartUtc = rangeStart;
            _loadedRangeEndUtc = rangeEnd;
        }

        /// <summary>
        /// Rebuilds <see cref="_eventsByDate"/> by re-filtering
        /// <see cref="_loadedEvents"/> through <see cref="_calendarVisibility"/>.
        /// Pure — no DB. Called from the calendar-visibility toggle path so a
        /// checkbox click never round-trips storage.
        /// </summary>
        private void ApplyVisibilityFilter()
        {
            // If _calendarVisibility is empty (no calendars), everything passes through.
            var visible = _loadedEvents
                .Where(e => _calendarVisibility.Count == 0
                            || _calendarVisibility.GetValueOrDefault(e.CalendarId, true));

            _eventsByDate = visible
                .GroupBy(e => DateHelpers.GetEventDayKey(e.StartTimeUtc))
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>
        /// Invalidates the event cache so the next
        /// <see cref="EnsureEventsLoadedAsync"/> call goes to the DB. Used by
        /// CRUD paths (event create/edit/delete, calendar delete) where the
        /// loaded events on disk no longer match what's in memory.
        /// </summary>
        private void InvalidateLoadedEvents()
        {
            _loadedRangeStartUtc = DateTime.MaxValue;
            _loadedRangeEndUtc = DateTime.MinValue;
        }

        // ── Calendar grid rendering ───────────────────────────────────────────

        private void RenderCalendarGrid()
        {
            _calendarGridRenderer.RenderCalendarGrid(
                _displayMonth, _selectedDate, _eventsByDate, _allCalendars);
        }

        // ── Week view rendering ───────────────────────────────────────────────

        private void RenderWeekView()
        {
            _weekViewRenderer.Render(
                _selectedDate, _eventsByDate, _allCalendars, showAllDayBand: true);
        }

        // ── ICalendarInteractionHost: empty-space activation ──────────────────

        /// <summary>
        /// Tapping an empty time slot in Week or Day View selects the day and
        /// opens the create-event popover pre-filled with the slot's start hour.
        /// </summary>
        public async void OnTimeSlotCreateRequested(DateTime dayDate, TimeSpan startTime)
        {
            SelectDate(dayDate);
            await CreateEventAsync(dayDate, dayDate.Date + startTime);
        }

        /// <summary>
        /// Tapping empty space in a Month View day cell selects the day and
        /// opens the create-event popover (defaulting to a 9am start). Tapping
        /// the day-number badge selects without creating.
        /// </summary>
        public async void OnDayCreateRequested(DateTime dayDate)
        {
            SelectDate(dayDate);
            await CreateEventAsync(dayDate, dayDate.Date.AddHours(9));
        }

        // ── Day view rendering ────────────────────────────────────────────────

        private void RenderDayView()
        {
            var dayKey = DateHelpers.GetLocalDayKey(_selectedDate);
            var events = _eventsByDate.GetValueOrDefault(dayKey) ?? new List<Event>();
            _dayViewRenderer.Render(_selectedDate, events, _allCalendars);
        }

        // ── ICalendarInteractionHost: event interaction ───────────────────────

        /// <summary>
        /// Event-chip tap (Month/Week/Day): shows the read-only popover
        /// anchored to the clicked chip. The popover's Edit/Delete buttons
        /// drive <see cref="EventPopover_EditRequested"/> and
        /// <see cref="EventPopover_DeleteRequested"/>.
        /// </summary>
        public void OnEventClicked(Event evt, FrameworkElement anchor)
        {
            var calendar = _allCalendars.FirstOrDefault(c => c.Id == evt.CalendarId);
            _eventPopover.SetEvent(evt, calendar);
            _eventPopoverFlyout.ShowAt(anchor);
        }

        /// <summary>
        /// Selected-day panel row click: opens the edit popover directly,
        /// bypassing the read-only popover (a deliberate product distinction).
        /// </summary>
        public async void OnEventActivated(Event evt) => await EditEventAsync(evt);

        private async void EventPopover_EditRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();
            await EditEventAsync(evt);
        }

        private async void EventPopover_DeleteRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();

            try
            {
                await _eventRepository.DeleteAsync(evt.Id);
                InvalidateLoadedEvents();
                await RefreshActiveViewAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error deleting event: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
