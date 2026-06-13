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
    public sealed partial class MainWindow : Window
    {
        private readonly EventRepository _eventRepository = new();
        private readonly CalendarRepository _calendarRepository = new();

        private readonly SidebarRenderer _sidebarRenderer;
        private readonly CalendarGridRenderer _calendarGridRenderer;
        private readonly MiniMonthRenderer _miniMonthRenderer;
        private readonly SelectedDayRenderer _selectedDayRenderer;
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
            _eventDialogService = new EventDialogService(
                _eventRepository, _calendarRepository, () => Content.XamlRoot, RefreshMonthAsync);
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

            DispatcherQueue.TryEnqueue(async () => await InitializeCalendarAsync());
        }

        // ── Initialization ────────────────────────────────────────────────────

        private async Task InitializeCalendarAsync()
        {
            try
            {
                // Load calendars + sidebar first so visibility state is ready
                // before RefreshMonthAsync filters events.
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
            await RefreshMonthAsync();
        }

        // ── Month refresh (single re-render entry point) ──────────────────────

        private async Task RefreshMonthAsync()
        {
            await LoadEventsAsync();
            _calendarGridRenderer.RenderDayHeaders();
            RenderCalendarGrid();
            RenderMiniMonth();
            RenderSelectedDay();
            UpdateMonthYearHeader();
        }

        private void UpdateMonthYearHeader()
        {
            MonthYearText.Text = _displayMonth.ToString("MMMM yyyy");
        }

        // ── Navigation handlers ───────────────────────────────────────────────

        private async void PrevMonthButton_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            await RefreshMonthAsync();
        }

        private async void NextMonthButton_Click(object sender, RoutedEventArgs e)
        {
            _displayMonth = _displayMonth.AddMonths(1);
            await RefreshMonthAsync();
        }

        private async void TodayButton_Click(object sender, RoutedEventArgs e)
        {
            var now = DateTime.Now;
            _displayMonth = new DateTime(now.Year, now.Month, 1);
            _selectedDate = DateHelpers.GetLocalDayKey(now);
            await RefreshMonthAsync();
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
            await RefreshMonthAsync();
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
                await RefreshMonthAsync();
            }
            else
            {
                SelectDate(date);
            }
        }

        private async void OnMiniMonthPrevMonth()
        {
            _displayMonth = _displayMonth.AddMonths(-1);
            await RefreshMonthAsync();
        }

        private async void OnMiniMonthNextMonth()
        {
            _displayMonth = _displayMonth.AddMonths(1);
            await RefreshMonthAsync();
        }

        // ── Selected day ──────────────────────────────────────────────────────

        /// <summary>
        /// Selects a day within the currently displayed month. Updates the
        /// shared <see cref="_selectedDate"/> state and refreshes only what
        /// depends on it (mini-month + grid highlights, selected-day panel) —
        /// no event reload, since the day is already in the loaded month.
        /// </summary>
        private void SelectDate(DateTime date)
        {
            var previousDate = _selectedDate;
            _selectedDate = DateHelpers.GetLocalDayKey(date);

            _miniMonthRenderer.UpdateSelectedDate(previousDate, _selectedDate);
            _calendarGridRenderer.UpdateSelectedDate(previousDate, _selectedDate);
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
            var (monthStart, monthEnd) = DateHelpers.GetMonthRangeUtc(_displayMonth);
            var events = await _eventRepository.GetInRangeAsync(monthStart, monthEnd);

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

        /// <summary>
        /// Double-tapping a day selects it and opens the create-event dialog.
        /// </summary>
        private async void OnDayActivated(DateTime dayDate)
        {
            SelectDate(dayDate);
            await _eventDialogService.ShowCreateEventDialogAsync(dayDate);
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
                await RefreshMonthAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error deleting event: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
