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

namespace Chronicle
{
    /// <summary>The main content view. Pure UI mode — not navigation state.</summary>
    internal enum CalendarView { Month, Week, Day }

    public sealed partial class MainWindow : Window
    {
        private readonly EventRepository _eventRepository = new();
        private readonly CalendarRepository _calendarRepository = new();

        private readonly SidebarRenderer _sidebarRenderer;
        private readonly CalendarGridRenderer _calendarGridRenderer;
        private readonly MiniMonthRenderer _miniMonthRenderer;
        private readonly SelectedDayRenderer _selectedDayRenderer;
        private readonly WeekViewRenderer _weekViewRenderer;
        private readonly DayViewRenderer _dayViewRenderer;
        private readonly EventDialogService _eventDialogService;
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

        private Dictionary<DateTime, List<Event>> _eventsByDate = new();

        public MainWindow()
        {
            InitializeComponent();

            var now = DateTime.Now;
            _displayMonth = new DateTime(now.Year, now.Month, 1);
            _selectedDate = DateHelpers.GetLocalDayKey(now);

            _sidebarRenderer = new SidebarRenderer(SidebarPanel);
            _calendarGridRenderer = new CalendarGridRenderer(DayNamesGrid, CalendarGrid);
            _miniMonthRenderer = new MiniMonthRenderer(MiniMonthPanel);
            _selectedDayRenderer = new SelectedDayRenderer(SelectedDayPanel);
            _weekViewRenderer = new WeekViewRenderer(WeekViewRoot);
            _dayViewRenderer = new DayViewRenderer(DayViewRoot);
            _eventDialogService = new EventDialogService(
                _eventRepository, _calendarRepository, () => Content.XamlRoot, RefreshActiveViewAsync);
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
        /// re-renders the sidebar, and refreshes the month views. Used at
        /// startup and after any create/edit/delete calendar operation.
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
            _sidebarRenderer.Render(
                _allCalendars,
                _calendarVisibility,
                OnCalendarVisibilityToggled,
                OnAddCalendar,
                OnEditCalendar,
                OnDeleteCalendar);
        }

        private async void OnCalendarVisibilityToggled(Guid calendarId, bool isVisible)
        {
            _calendarVisibility[calendarId] = isVisible;
            await RefreshActiveViewAsync();
        }

        // ── Calendar management ───────────────────────────────────────────────

        private async void OnAddCalendar()
        {
            await _calendarDialogService.ShowCreateCalendarDialogAsync();
        }

        private async void OnEditCalendar(Calendar calendar)
        {
            await _calendarDialogService.ShowEditCalendarDialogAsync(calendar);
        }

        private async void OnDeleteCalendar(Calendar calendar)
        {
            await _calendarDialogService.ShowDeleteCalendarDialogAsync(calendar);
        }

        // ── Mini month navigator ──────────────────────────────────────────────

        private void RenderMiniMonth()
        {
            _miniMonthRenderer.Render(
                _displayMonth,
                _selectedDate,
                OnMiniMonthDateSelected,
                OnMiniMonthPrevMonth,
                OnMiniMonthNextMonth);
        }

        /// <summary>
        /// A day was clicked in the mini month. Update the selected date and,
        /// if the clicked day belongs to a different month (e.g. a trailing/
        /// leading adjacent-month day), move the displayed month to match.
        /// </summary>
        private async void OnMiniMonthDateSelected(DateTime date)
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

        private async void OnMiniMonthPrevMonth()
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            await RefreshActiveViewAsync();
        }

        private async void OnMiniMonthNextMonth()
        {
            _displayMonth = _displayMonth.AddMonths(1);
            await RefreshActiveViewAsync();
        }

        // ── Selected day ──────────────────────────────────────────────────────

        /// <summary>
        /// Selects a day, updating the shared <see cref="_selectedDate"/> and the
        /// views that depend on it. The fast path updates selection highlights
        /// incrementally without reloading events (the day is already loaded).
        ///
        /// In Week View, selecting a day in a different week changes which seven
        /// days are visible and may cross the loaded range, so that case falls
        /// back to a full refresh.
        /// </summary>
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
            _weekViewRenderer.UpdateSelectedDate(previousDate, _selectedDate);
            RenderSelectedDay();
        }

        private void RenderSelectedDay()
        {
            var dayKey = DateHelpers.GetLocalDayKey(_selectedDate);
            var events = _eventsByDate.GetValueOrDefault(dayKey) ?? new List<Event>();
            _selectedDayRenderer.Render(
                _selectedDate, events, _allCalendars, OnSelectedDayEventClicked);
        }

        private async void OnSelectedDayEventClicked(Event evt)
        {
            await _eventDialogService.ShowEditEventDialogAsync(evt);
        }

        // ── Event loading ─────────────────────────────────────────────────────

        private async Task LoadEventsAsync()
        {
            // Same pipeline for every view; only the queried range differs.
            // Week/Day derive their range from _selectedDate (and can straddle a
            // month boundary); Month uses _displayMonth.
            var (rangeStart, rangeEnd) = _currentView switch
            {
                CalendarView.Week => DateHelpers.GetWeekRangeUtc(_selectedDate),
                CalendarView.Day => DateHelpers.GetDayRangeUtc(_selectedDate),
                _ => DateHelpers.GetMonthRangeUtc(_displayMonth)
            };

            var events = await _eventRepository.GetInRangeAsync(rangeStart, rangeEnd);

            // Filter to calendars that are currently visible.
            // If _calendarVisibility is empty (no calendars), everything passes through.
            var visible = events
                .Where(e => _calendarVisibility.Count == 0
                            || _calendarVisibility.GetValueOrDefault(e.CalendarId, true));

            _eventsByDate = visible
                .GroupBy(e => DateHelpers.GetEventDayKey(e.StartTimeUtc))
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // ── Calendar grid rendering ───────────────────────────────────────────

        private void RenderCalendarGrid()
        {
            _calendarGridRenderer.RenderCalendarGrid(
                _displayMonth,
                _selectedDate,
                _eventsByDate,
                _allCalendars,
                onDaySelected: SelectDate,
                onDayActivated: OnDayActivated,
                onEventClicked: ShowEventPopover);
        }

        // ── Week view rendering ───────────────────────────────────────────────

        private void RenderWeekView()
        {
            _weekViewRenderer.Render(
                _selectedDate,
                _eventsByDate,
                _allCalendars,
                onDaySelected: SelectDate,
                onDayActivated: OnDayActivated,
                onEventClicked: ShowEventPopover);
        }

        /// <summary>
        /// Double-tapping a day selects it and opens the create-event dialog.
        /// </summary>
        private async void OnDayActivated(DateTime dayDate)
        {
            SelectDate(dayDate);
            await _eventDialogService.ShowCreateEventDialogAsync(dayDate);
        }

        // ── Day view rendering ────────────────────────────────────────────────

        private void RenderDayView()
        {
            var dayKey = DateHelpers.GetLocalDayKey(_selectedDate);
            var events = _eventsByDate.GetValueOrDefault(dayKey) ?? new List<Event>();
            _dayViewRenderer.Render(
                _selectedDate,
                events,
                _allCalendars,
                onEventClicked: ShowEventPopover,
                onCreateAt: OnDayTimeActivated);
        }

        /// <summary>
        /// Double-tapping an empty time slot in Day View opens the create-event
        /// dialog for the selected day, pre-filled with the slot's start hour.
        /// </summary>
        private async void OnDayTimeActivated(TimeSpan startTime)
        {
            await _eventDialogService.ShowCreateEventDialogAsync(_selectedDate, startTime);
        }

        // ── Event popover ─────────────────────────────────────────────────────

        /// <summary>
        /// Shows the read-only event popover anchored to the clicked event chip.
        /// The popover's Edit/Delete buttons drive <see cref="EventPopover_EditRequested"/>
        /// and <see cref="EventPopover_DeleteRequested"/>; clicking elsewhere or
        /// the Close button dismisses it without further action.
        /// </summary>
        private void ShowEventPopover(Event evt, FrameworkElement anchor)
        {
            var calendar = _allCalendars.FirstOrDefault(c => c.Id == evt.CalendarId);
            _eventPopover.SetEvent(evt, calendar);
            _eventPopoverFlyout.ShowAt(anchor);
        }

        private async void EventPopover_EditRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();
            await _eventDialogService.ShowEditEventDialogAsync(evt);
        }

        private async void EventPopover_DeleteRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();

            try
            {
                await _eventRepository.DeleteAsync(evt.Id);
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
