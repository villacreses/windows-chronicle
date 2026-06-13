using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Views.Dialogs;
using Chronicle.Views.Rendering;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        private readonly EventDialogService _eventDialogService;

        private DateTime _displayMonth;

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

            _sidebarRenderer = new SidebarRenderer(SidebarPanel);
            _calendarGridRenderer = new CalendarGridRenderer(DayNamesGrid, CalendarGrid);
            _eventDialogService = new EventDialogService(
                _eventRepository, _calendarRepository, () => Content.XamlRoot, RefreshMonthAsync);

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
                // Load calendars first so visibility state and sidebar are ready
                // before RefreshMonthAsync filters events.
                _allCalendars = await _calendarRepository.GetAllAsync();

                // Seed every calendar as visible.
                foreach (var cal in _allCalendars)
                    _calendarVisibility.TryAdd(cal.Id, true);

                RenderSidebar();

                await RefreshMonthAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error initializing calendar: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Month refresh (single re-render entry point) ──────────────────────

        private async Task RefreshMonthAsync()
        {
            await LoadEventsAsync();
            _calendarGridRenderer.RenderDayHeaders();
            RenderCalendarGrid();
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
            await RefreshMonthAsync();
        }

        // ── Sidebar ───────────────────────────────────────────────────────────

        /// <summary>
        /// Renders the Calendars section in the sidebar from <see cref="_allCalendars"/>.
        /// Called once at startup. Call again if calendars are ever added/removed via UI.
        /// </summary>
        private void RenderSidebar()
        {
            _sidebarRenderer.Render(_allCalendars, _calendarVisibility, OnCalendarVisibilityToggled);
        }

        private async void OnCalendarVisibilityToggled(Guid calendarId, bool isVisible)
        {
            _calendarVisibility[calendarId] = isVisible;
            await RefreshMonthAsync();
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
                _eventsByDate,
                _allCalendars,
                onDayClicked: async dayDate => await _eventDialogService.ShowCreateEventDialogAsync(dayDate),
                onEventClicked: async evt => await _eventDialogService.ShowEditEventDialogAsync(evt));
        }
    }
}
