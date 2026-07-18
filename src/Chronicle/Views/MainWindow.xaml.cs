using Chronicle.Data.Repositories;
using Chronicle.Helpers;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using Chronicle.Notifications;
using Chronicle.Projection;
using Chronicle.Views.Dialogs;
using Chronicle.Views.Popovers;
using Chronicle.Views.Rendering;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Chronicle
{
    /// <summary>The main content view. Pure UI mode — not navigation state.</summary>
    internal enum CalendarView { Month, Week, Day, Agenda, Year }

    public sealed partial class MainWindow : Window, ICalendarInteractionHost, ISidebarHost
    {
        private readonly EventRepository _eventRepository = new();
        private readonly CalendarRepository _calendarRepository = new();
        private readonly OverrideRepository _overrideRepository = new();
        private readonly ReminderRepository _reminderRepository = new();

        // The sole owner of the OS scheduled-toast cache. Behind the seam so
        // the notification subsystem owns its platform APIs in one place —
        // not to hide Windows, but so toast concepts never leak back into the
        // reminder model and no other code mutates scheduled toasts.
        private readonly IReminderScheduler _reminderScheduler =
            new ScheduledToastReminderScheduler();

        private readonly SidebarRenderer _sidebarRenderer;
        private readonly CalendarGridRenderer _calendarGridRenderer;
        private readonly MiniMonthRenderer _miniMonthRenderer;
        private readonly SelectedDayRenderer _selectedDayRenderer;
        private readonly WeekViewRenderer _weekViewRenderer;
        private readonly DayViewRenderer _dayViewRenderer;
        private readonly AgendaViewRenderer _agendaViewRenderer;
        private readonly YearViewRenderer _yearViewRenderer;
        private readonly CalendarDialogService _calendarDialogService;

        private readonly EventPopover _eventPopover;
        private readonly Flyout _eventPopoverFlyout;

        // Search results renderer + its flyout host. Kept as fields so the
        // panel and flyout live for the window's lifetime (constructed once,
        // content swapped per query). The flyout is anchored to SearchBox
        // when shown.
        private readonly StackPanel _searchResultsPanel;
        private readonly Flyout _searchResultsFlyout;
        private readonly SearchResultsRenderer _searchResultsRenderer;

        // Search range: -1 year to +5 years around today. Standalones outside
        // this window are excluded (LIKE is cheap but historical/far-future
        // hits are noise for a calendar). Recurring expansion is likewise
        // bounded here. If a user reports missing an event outside this
        // window, revisit — the range is a UI default, not a hard invariant.
        private static readonly TimeSpan SearchPastWindow = TimeSpan.FromDays(365);
        private static readonly TimeSpan SearchFutureWindow = TimeSpan.FromDays(365 * 5);

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

        // Events for the currently loaded range, before the visibility filter.
        // Contains:
        //   - standalone events that overlap the range
        //   - expanded recurrence occurrences (transient Event instances
        //     synthesized from recurring masters by RecurrenceExpander)
        // Recurring master rows are NOT stored here — only their expansions
        // enter the projection. `_eventsByDate` is derived from this plus
        // `_calendarVisibility`; visibility toggles re-filter without
        // re-expanding. The loaded range tracks what the cache covers so
        // EnsureEventsLoadedAsync can skip the query when the active view's
        // range fits inside it (e.g., switching Month → Week → Day in place).
        private List<Event> _projectedEvents = new();
        private DateTime _loadedRangeStartUtc = DateTime.MaxValue;
        private DateTime _loadedRangeEndUtc = DateTime.MinValue;

        // Render-time projection cache (per the recurrence model in
        // DECISIONS.md): the day-grouped view of `_projectedEvents` after
        // visibility filtering. Never an identity source — UI state that
        // needs to remember a selection across reloads must hold an
        // `EventKey`, not a dictionary slot or list index.
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
            _agendaViewRenderer = new AgendaViewRenderer(AgendaViewRoot, this);
            _yearViewRenderer = new YearViewRenderer(YearViewRoot, this);
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

            _searchResultsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                Width = 340
            };
            _searchResultsFlyout = new Flyout
            {
                Content = new ScrollViewer
                {
                    Content = _searchResultsPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 420
                },
                Placement = FlyoutPlacementMode.Bottom
            };
            _searchResultsRenderer = new SearchResultsRenderer(
                _searchResultsPanel, OnSearchResultActivated);

            PrevMonthButton.Click += PrevMonthButton_Click;
            NextMonthButton.Click += NextMonthButton_Click;
            TodayButton.Click += TodayButton_Click;

            MonthViewToggle.Click += (s, e) => SwitchView(CalendarView.Month);
            WeekViewToggle.Click += (s, e) => SwitchView(CalendarView.Week);
            DayViewToggle.Click += (s, e) => SwitchView(CalendarView.Day);
            AgendaViewToggle.Click += (s, e) => SwitchView(CalendarView.Agenda);
            YearViewToggle.Click += (s, e) => SwitchView(CalendarView.Year);

            SearchBox.QuerySubmitted += SearchBox_QuerySubmitted;

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
            // Enforce the "always at least one calendar" invariant before the
            // first load and after every calendar mutation (including deleting
            // the last one, which routes back through here): a fresh or emptied
            // database self-heals with a "Default" calendar instead of leaving
            // the UI in a dead-end empty state where day-clicks silently no-op.
            await _calendarRepository.EnsureDefaultAsync();

            _allCalendars = await _calendarRepository.GetAllAsync();

            foreach (var cal in _allCalendars)
                _calendarVisibility.TryAdd(cal.Id, true);

            var existingIds = _allCalendars.Select(c => c.Id).ToHashSet();
            foreach (var staleId in _calendarVisibility.Keys.Where(id => !existingIds.Contains(id)).ToList())
                _calendarVisibility.Remove(staleId);

            RenderSidebar();
            await AfterDataMutationAsync();
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
                case CalendarView.Agenda:
                    RenderAgendaView();
                    break;
                case CalendarView.Year:
                    RenderYearView();
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
                CalendarView.Agenda => "Upcoming",
                CalendarView.Year => _displayMonth.Year.ToString(),
                _ => _displayMonth.ToString("MMMM yyyy")
            };

            // Agenda is anchored to today, not a paged frame — Prev/Next
            // have no meaningful action here. Disabling makes that visible
            // rather than silently no-op.
            var pagingEnabled = _currentView != CalendarView.Agenda;
            PrevMonthButton.IsEnabled = pagingEnabled;
            NextMonthButton.IsEnabled = pagingEnabled;
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
                case CalendarView.Year:
                    _displayMonth = _displayMonth.AddYears(direction);
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

            ApplyViewMode(view);
            await RefreshActiveViewAsync();
        }

        /// <summary>
        /// Sets the active view mode synchronously — state, toggle buttons,
        /// and root visibilities — without triggering a refresh. Callers that
        /// need to await the subsequent load (e.g. the reminder deep-link)
        /// call this then <see cref="RefreshActiveViewAsync"/> directly, since
        /// <see cref="SwitchView"/> is fire-and-forget (async void).
        /// </summary>
        private void ApplyViewMode(CalendarView view)
        {
            _currentView = view;
            UpdateViewToggles();

            MonthViewRoot.Visibility =
                view == CalendarView.Month ? Visibility.Visible : Visibility.Collapsed;
            WeekViewRoot.Visibility =
                view == CalendarView.Week ? Visibility.Visible : Visibility.Collapsed;
            DayViewRoot.Visibility =
                view == CalendarView.Day ? Visibility.Visible : Visibility.Collapsed;
            AgendaViewRoot.Visibility =
                view == CalendarView.Agenda ? Visibility.Visible : Visibility.Collapsed;
            YearViewRoot.Visibility =
                view == CalendarView.Year ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateViewToggles()
        {
            MonthViewToggle.IsChecked = _currentView == CalendarView.Month;
            WeekViewToggle.IsChecked = _currentView == CalendarView.Week;
            DayViewToggle.IsChecked = _currentView == CalendarView.Day;
            AgendaViewToggle.IsChecked = _currentView == CalendarView.Agenda;
            YearViewToggle.IsChecked = _currentView == CalendarView.Year;
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
        /// The most recently tapped event chip, captured in
        /// <see cref="OnEventClicked"/>. <see cref="EventPopover_EditRequested"/>
        /// reuses it as the anchor for the edit popover so the editor opens as
        /// a continuation of the read-only popover's talk-bubble.
        /// </summary>
        private FrameworkElement? _lastEventAnchor;

        /// <summary>
        /// Fallback anchor when no chip is available (selected-day panel rows).
        /// </summary>
        private FrameworkElement FallbackAnchor =>
            Content as FrameworkElement ?? throw new InvalidOperationException("Window has no content root.");

        /// <summary>
        /// The active main view's root container — the subtree the draft chip
        /// lives in for the current view. Used as both the scan root for
        /// <see cref="FindChipForEventAsync"/> and the secondary anchor when the
        /// chip can't be located (defensive; a re-rendered draft should always
        /// be findable).
        /// </summary>
        private FrameworkElement ActiveViewRoot => _currentView switch
        {
            CalendarView.Week => WeekViewRoot,
            CalendarView.Day => DayViewRoot,
            CalendarView.Agenda => AgendaViewRoot,
            CalendarView.Year => YearViewRoot,
            _ => MonthViewRoot
        };

        /// <summary>
        /// Resolves the anchor + placement for an edit/create popover. Day View
        /// chips span the full width of the main section, so edge-aligning the
        /// popover against one crams it into the window margin and overflows the
        /// form. For anchors inside the Day timeline we keep the chip as anchor
        /// but place the popover <see cref="FlyoutPlacementMode.Top"/> of it —
        /// Top centers the flyout horizontally over its target, and since the
        /// target spans the main section that lands the popover centered on it
        /// (WinUI flips it below automatically if the chip is too near the top).
        /// Everything else (Month/Week chips, selected-day sidebar rows — even
        /// while Day View is active) keeps the right-edge talk-bubble.
        /// </summary>
        private (FrameworkElement anchor, FlyoutPlacementMode placement) ResolvePopoverAnchor(
            FrameworkElement naturalAnchor)
        {
            if (IsDescendantOrSelf(naturalAnchor, DayViewRoot))
                return (naturalAnchor, FlyoutPlacementMode.Top);
            return (naturalAnchor, FlyoutPlacementMode.RightEdgeAlignedTop);
        }

        /// <summary>
        /// True if <paramref name="node"/> is <paramref name="ancestor"/> or sits
        /// anywhere beneath it in the visual tree.
        /// </summary>
        private static bool IsDescendantOrSelf(DependencyObject? node, DependencyObject ancestor)
        {
            for (; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, ancestor))
                    return true;
            return false;
        }

        /// <summary>
        /// Locates the chip whose <see cref="EventTapTarget"/> matches
        /// <paramref name="key"/> in the active view's subtree, deferring
        /// until layout has settled so Month View's <c>SizeChanged</c>-driven
        /// chip insertion has run. Week/Day timelines insert chips
        /// synchronously, so the deferral is a no-op there but keeps a single
        /// code path for both. Keys by <see cref="EventKey"/> so expanded
        /// occurrences of the same series resolve to distinct chips.
        /// </summary>
        private Task<FrameworkElement?> FindChipForEventAsync(EventKey key)
        {
            var tcs = new TaskCompletionSource<FrameworkElement?>();
            // Low priority queues behind any pending layout/render work, so
            // Month View's deferred FillEventsArea has inserted its chips by
            // the time we walk the tree.
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                tcs.SetResult(FindChipForEvent(ActiveViewRoot, key)));
            return tcs.Task;
        }

        /// <summary>
        /// Depth-first scan for a <see cref="FrameworkElement"/> whose
        /// <see cref="FrameworkElement.Tag"/> carries an
        /// <see cref="EventTapTarget"/> matching <paramref name="key"/>.
        /// Both event chips and timeline event blocks tag themselves this way
        /// (see <see cref="CalendarRenderHelper.CreateEventChip"/> and
        /// <see cref="TimelineRenderHelper"/>), so one walker covers Month,
        /// Week, and Day.
        /// </summary>
        private static FrameworkElement? FindChipForEvent(DependencyObject root, EventKey key)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is FrameworkElement fe
                    && fe.Tag is EventTapTarget t
                    && EventKey.For(t.Event) == key)
                    return fe;
                var found = FindChipForEvent(child, key);
                if (found is not null)
                    return found;
            }
            return null;
        }

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

            // Anchor the create popover to the freshly-rendered draft chip so
            // it reads the same as the edit popover (anchored to its event
            // chip) — uniform talk-bubble behavior across both flows. Falls
            // back to the active view root if the chip can't be located.
            var naturalAnchor = await FindChipForEventAsync(EventKey.For(draft)) ?? ActiveViewRoot;
            var (anchor, placement) = ResolvePopoverAnchor(naturalAnchor);

            EventEditResult? created;
            try
            {
                created = await EventEditPopover.ShowCreateEventAsync(
                    anchor, suggestedStartLocal, _allCalendars, placement);
            }
            finally
            {
                RemoveDraft(draft);
            }

            if (created is not null)
            {
                await _eventRepository.InsertAsync(created.Event);
                // Reminders are a child collection of the event aggregate;
                // persisted separately after the event row exists (FK). The
                // scheduler reconciles off this state in unit 3 — nothing
                // schedules a toast here.
                await _reminderRepository.SetForEventAsync(
                    created.Event.Id, created.Reminders);
                await AfterDataMutationAsync();
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
                RecurrenceRule = null,
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
                case CalendarView.Agenda:
                    RenderAgendaView();
                    break;
                case CalendarView.Year:
                    RenderYearView();
                    break;
            }
            RenderSelectedDay();
        }

        /// <summary>
        /// Dispatch entry for editing a chip. Constructs the
        /// <see cref="EventRef"/> from the chip's <see cref="Event"/>, then
        /// routes:
        /// <list type="bullet">
        /// <item>Standalone / master → master-edit popover (existing flow).</item>
        /// <item>Occurrence → scope picker dialog, then either the master-edit
        /// flow (All-events) or the occurrence-edit flow that writes an
        /// override row (This-event).</item>
        /// </list>
        /// The anchor and placement are resolved via
        /// <see cref="ResolvePopoverAnchor"/> so Day View centers instead of
        /// edge-aligning. No-op if any prompt is dismissed.
        /// </summary>
        private async Task EditEventAsync(Event evt, FrameworkElement naturalAnchor)
        {
            var (anchor, placement) = ResolvePopoverAnchor(naturalAnchor);

            switch (EventRef.From(evt))
            {
                case EventRef.Master:
                    // Standalone or master row (the latter shouldn't reach
                    // here in practice — masters don't enter _eventsByDate —
                    // but the type allows it cleanly). We already have the
                    // Event in hand; no fetch needed.
                    await EditMasterAsync(evt, anchor, placement);
                    return;

                case EventRef.Occurrence occurrence:
                    var scope = await PromptEditScopeAsync(evt.Title);
                    switch (scope)
                    {
                        case EditScope.AllEvents:
                            await EditMasterByIdAsync(occurrence.SeriesId, anchor, placement);
                            return;

                        case EditScope.ThisEvent:
                            await EditOccurrenceAsync(occurrence, evt, anchor, placement);
                            return;

                        default: // Cancel / dismissed
                            return;
                    }
            }
        }

        private enum EditScope { ThisEvent, AllEvents, Cancel }

        /// <summary>
        /// Scope picker for recurring-event edits. ContentDialog with
        /// "This event" / "All events" / "Cancel". Mirrors the delete
        /// scope dialog so the user sees a consistent affordance for
        /// both occurrence-scoped operations.
        /// </summary>
        private async Task<EditScope> PromptEditScopeAsync(string eventTitle)
        {
            var dialog = new ContentDialog
            {
                Title = "Edit recurring event",
                Content = $"\"{eventTitle}\" is part of a recurring series. "
                        + "Edit just this occurrence, or the entire series?",
                PrimaryButtonText = "This event",
                SecondaryButtonText = "All events",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            return result switch
            {
                ContentDialogResult.Primary => EditScope.ThisEvent,
                ContentDialogResult.Secondary => EditScope.AllEvents,
                _ => EditScope.Cancel,
            };
        }

        /// <summary>
        /// Master-edit branch when the caller already has the master Event
        /// in hand (standalone case, or freshly fetched). Opens the
        /// existing all-events editor and persists via the event repository.
        /// </summary>
        private async Task EditMasterAsync(
            Event master,
            FrameworkElement anchor,
            FlyoutPlacementMode placement)
        {
            // Reminders are a side collection — load them to seed the picker,
            // since the Event does not carry them.
            var existingReminders = await _reminderRepository.GetForEventAsync(master.Id);

            var edited = await EventEditPopover.ShowEditEventAsync(
                anchor, master, existingReminders, _allCalendars, placement);
            if (edited is null)
                return;

            await _eventRepository.UpdateAsync(edited.Event);
            // Replace the event's whole reminder set (0..1 from the editor).
            await _reminderRepository.SetForEventAsync(
                edited.Event.Id, edited.Reminders);
            await AfterDataMutationAsync();
        }

        /// <summary>
        /// Master-edit branch when the caller has only the series id
        /// (occurrence → All-events scope). Fetches the master row, then
        /// delegates to <see cref="EditMasterAsync"/>. Missing master
        /// triggers a refresh so the stale projection drops.
        /// </summary>
        private async Task EditMasterByIdAsync(
            Guid masterId,
            FrameworkElement anchor,
            FlyoutPlacementMode placement)
        {
            var master = await _eventRepository.GetByIdAsync(masterId);
            if (master is null)
            {
                await AfterDataMutationAsync();
                return;
            }

            await EditMasterAsync(master, anchor, placement);
        }

        /// <summary>
        /// Occurrence-edit branch (This-event scope). Opens the stripped
        /// occurrence-edit popover pre-filled from the merged occurrence
        /// values, converts the result into <see cref="OverrideFields"/>,
        /// and writes via <see cref="OverrideRepository.UpsertAsync"/>.
        /// All five override-eligible fields (Title, Description,
        /// StartTimeUtc, EndTimeUtc, IsAllDay) are snapshotted from the
        /// editor result; the expander's merge treats each as an
        /// authoritative per-occurrence value.
        /// </summary>
        private async Task EditOccurrenceAsync(
            EventRef.Occurrence target,
            Event occurrence,
            FrameworkElement anchor,
            FlyoutPlacementMode placement)
        {
            var edited = await EventEditPopover.ShowEditOccurrenceAsync(
                anchor, occurrence, placement);
            if (edited is null)
                return;

            var fields = new OverrideFields(
                Title: edited.Title,
                Description: edited.Description,
                StartTimeUtc: edited.StartTimeUtc,
                EndTimeUtc: edited.EndTimeUtc,
                IsAllDay: edited.IsAllDay);

            await _overrideRepository.UpsertAsync(target, fields);
            await AfterDataMutationAsync();
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
                CalendarView.Agenda => DateHelpers.GetAgendaRangeUtc(DateTime.Now),
                CalendarView.Year => DateHelpers.GetYearRangeUtc(_displayMonth),
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

            if (EventProjection.RangeCovered(
                    _loadedRangeStartUtc, _loadedRangeEndUtc, rangeStart, rangeEnd))
                return;

            // Two queries per range change, regardless of master count.
            // Visibility toggles and view switches inside the loaded range
            // hit neither (the cache short-circuit above covers them).
            var rows = await _eventRepository.GetInRangeAsync(rangeStart, rangeEnd);

            var recurringMasterIds = new List<Guid>();
            foreach (var row in rows)
            {
                if (row.RecurrenceRule is not null)
                    recurringMasterIds.Add(row.Id);
            }

            var overrides = recurringMasterIds.Count == 0
                ? new List<EventOverride>()
                : await _overrideRepository.GetForSeriesAsync(recurringMasterIds);

            var overridesBySeries = EventProjection.GroupOverridesBySeries(overrides);

            _projectedEvents = EventProjection.ExpandRecurrences(
                rows, rangeStart, rangeEnd, overridesBySeries);
            _loadedRangeStartUtc = rangeStart;
            _loadedRangeEndUtc = rangeEnd;
        }

        /// <summary>
        /// Rebuilds <see cref="_eventsByDate"/> by re-filtering
        /// <see cref="_projectedEvents"/> through <see cref="_calendarVisibility"/>.
        /// Pure — no DB. Called from the calendar-visibility toggle path so a
        /// checkbox click never round-trips storage.
        /// </summary>
        private void ApplyVisibilityFilter()
        {
            _eventsByDate = EventProjection.GroupVisibleByDay(
                _projectedEvents, _calendarVisibility);
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

        /// <summary>
        /// The single "persisted calendar data changed" chokepoint. Every
        /// event-mutation path (create, edit-master, edit-occurrence, delete,
        /// skip-occurrence) routes through here, and the calendar-mutation
        /// path funnels into it via <see cref="ReloadCalendarsAndRefreshAsync"/>.
        /// Consolidating the post-mutation behavior in one place keeps the
        /// invalidate-then-refresh pair from drifting across handlers, and
        /// gives later cross-cutting concerns (e.g. reminder-schedule
        /// reconciliation) a single hook rather than N scattered call sites.
        ///
        /// Pure navigation (view switch, date selection) deliberately does
        /// NOT come through here — it calls <see cref="RefreshActiveViewAsync"/>
        /// without invalidating, because the data hasn't changed.
        /// </summary>
        private async Task AfterDataMutationAsync()
        {
            InvalidateLoadedEvents();
            await RefreshActiveViewAsync();

            // Reminder scheduling is ONE downstream consumer of this generic
            // lifecycle event — not the reason the chokepoint exists. Run it
            // after the view refresh so a slow or failing schedule never
            // delays the visible update. Other future consumers (provider-sync
            // bookkeeping, search indexing) would hang off the same event.
            await ReconcileRemindersAsync();
        }

        // ── Reminder activation (deep-link from a clicked toast) ──────────────

        /// <summary>
        /// Translates a clicked reminder toast into the existing navigation
        /// model: go to the event's day and open it. Deliberately thin — it
        /// knows nothing of scheduling, reminders, or recurrence expansion.
        /// The target date comes straight from the <see cref="EventRef"/>
        /// (an occurrence's rule-walk anchor, or a loaded master's start), and
        /// the event to open is found by identity against the projection the
        /// normal load pipeline already expanded — activation never expands
        /// anything itself.
        ///
        /// Best-effort by design: if the event was deleted since the toast was
        /// scheduled, the user simply lands on the day with nothing to open.
        /// </summary>
        public async Task DeepLinkToReminderAsync(EventRef reminderRef)
        {
            DateTime targetDate;
            Guid eventId;
            DateTime? anchorUtc = null;

            switch (reminderRef)
            {
                case EventRef.Occurrence occ:
                    eventId = occ.SeriesId;
                    anchorUtc = occ.AnchorUtc;
                    targetDate = DateHelpers.GetEventDayKey(occ.AnchorUtc);
                    break;

                case EventRef.Master master:
                    var evt = await _eventRepository.GetByIdAsync(master.Id);
                    if (evt is null)
                        return; // deleted since scheduling — nothing to navigate to.
                    eventId = master.Id;
                    targetDate = DateHelpers.GetEventDayKey(evt.StartTimeUtc);
                    break;

                default:
                    return;
            }

            // Navigate with the existing model, awaiting the load so the day's
            // events are in the projection before we look for the target.
            _selectedDate = targetDate;
            _displayMonth = DateHelpers.GetMonthStartLocal(targetDate);
            ApplyViewMode(CalendarView.Day);
            await RefreshActiveViewAsync();

            // Identity match against the already-expanded projection — an
            // occurrence matches on (Id + anchor), a master/standalone on Id
            // with no anchor.
            var dayEvents = _eventsByDate.GetValueOrDefault(targetDate) ?? new List<Event>();
            var match = dayEvents.FirstOrDefault(e =>
                e.Id == eventId
                && (anchorUtc is null ? !e.IsOccurrence : e.SeriesAnchorUtc == anchorUtc));

            if (match is null)
                return;

            var anchor = await FindChipForEventAsync(EventKey.For(match)) ?? FallbackAnchor;
            OnEventClicked(match, anchor);
        }

        // ── Reminder schedule reconciliation ──────────────────────────────────

        // Horizon policy (REMINDERS.md): schedule reminders whose fire time
        // falls within this window from now. A tunable policy, not an invariant.
        private static readonly TimeSpan ReminderHorizon = TimeSpan.FromDays(60);

        // Expansion pad: an event can start past the horizon yet have a reminder
        // firing inside it (a large lead). Expand comfortably beyond the
        // editor's largest preset (2 weeks) so no in-window reminder is missed.
        private static readonly TimeSpan ReminderHorizonPad = TimeSpan.FromDays(31);

        /// <summary>
        /// Recomputes the desired reminder schedule from persisted state and
        /// hands it to the scheduler, which reconciles the OS toast cache to
        /// match. This is the single place the reminder projection is computed
        /// and handed to the notification boundary; the scheduler is the single
        /// place the OS cache is mutated.
        ///
        /// Runs its own load over the HORIZON range — independent of the active
        /// view's loaded range, since reminders need ~60 days ahead, not the
        /// current month. Reuses the same projection pipeline as the views and
        /// search (<see cref="EventRepository.GetInRangeAsync"/> →
        /// <see cref="EventProjection.ExpandRecurrences"/> →
        /// <see cref="EventProjection.ReminderSchedule"/>); it duplicates no
        /// projection logic.
        ///
        /// Failure contract (REMINDERS.md): a scheduling failure must never
        /// abort the mutation that triggered it. Failures are caught and logged;
        /// the app stays usable and the next reconcile repairs the schedule. If
        /// Chronicle grows a diagnostics/sync-status surface, reminder failures
        /// would feed into it here.
        /// </summary>
        private async Task ReconcileRemindersAsync()
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var windowStartUtc = nowUtc;
                var windowEndUtc = nowUtc + ReminderHorizon;
                var expandEndUtc = windowEndUtc + ReminderHorizonPad;

                // Own load over the horizon (not _eventsByDate — that is the
                // active view's range).
                var rows = await _eventRepository.GetInRangeAsync(
                    windowStartUtc, expandEndUtc);

                var recurringMasterIds = rows
                    .Where(e => e.RecurrenceRule is not null)
                    .Select(e => e.Id)
                    .ToList();
                var overrides = recurringMasterIds.Count == 0
                    ? new List<EventOverride>()
                    : await _overrideRepository.GetForSeriesAsync(recurringMasterIds);
                var overridesBySeries = EventProjection.GroupOverridesBySeries(overrides);

                var expanded = EventProjection.ExpandRecurrences(
                    rows, windowStartUtc, expandEndUtc, overridesBySeries);

                var eventIds = expanded.Select(e => e.Id).Distinct().ToList();
                var reminders = await _reminderRepository.GetForEventsAsync(eventIds);
                var remindersByEvent = EventProjection.GroupRemindersByEvent(reminders);

                var schedule = EventProjection.ReminderSchedule(
                    expanded, remindersByEvent, windowStartUtc, windowEndUtc);

                await _reminderScheduler.ReconcileAsync(schedule);
            }
            catch (Exception ex)
            {
                // Non-fatal: reminders may be stale until the next reconcile.
                System.Diagnostics.Debug.WriteLine(
                    "Reminder reconcile failed (reminders may be out of sync "
                    + $"until the next reconcile): {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ── Search ────────────────────────────────────────────────────────────

        /// <summary>
        /// Query-submitted (Enter or search-icon click) on the header
        /// search box. Runs the two-layer search pipeline:
        /// <see cref="EventRepository.SearchCandidatesAsync"/> for candidate
        /// rows, then <see cref="EventProjection.SearchOccurrences"/> to
        /// expand recurring masters and re-filter merged occurrences. Shows
        /// the results flyout anchored to the search box.
        ///
        /// Empty query hides the flyout — a submitted empty query is a
        /// "cancel search" gesture.
        /// </summary>
        private async void SearchBox_QuerySubmitted(
            AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            var query = (args.QueryText ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                _searchResultsFlyout.Hide();
                return;
            }

            try
            {
                var nowUtc = DateTime.UtcNow;
                var rangeStart = nowUtc - SearchPastWindow;
                var rangeEnd = nowUtc + SearchFutureWindow;

                var candidates = await _eventRepository.SearchCandidatesAsync(
                    query, rangeStart, rangeEnd);

                var recurringMasterIds = candidates
                    .Where(e => e.RecurrenceRule is not null)
                    .Select(e => e.Id)
                    .ToList();

                var overrides = recurringMasterIds.Count == 0
                    ? new List<EventOverride>()
                    : await _overrideRepository.GetForSeriesAsync(recurringMasterIds);
                var overridesBySeries = EventProjection.GroupOverridesBySeries(overrides);

                var results = EventProjection.SearchOccurrences(
                    candidates, overridesBySeries, query, rangeStart, rangeEnd);

                _searchResultsRenderer.Render(results, _allCalendars);
                _searchResultsFlyout.ShowAt(SearchBox);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Search failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// A search result was clicked. Navigate the calendar to the
        /// event's day in Day View so the user sees it in context, then
        /// open the edit popover (matching the selected-day panel's
        /// activation gesture). The popover is anchored to the fallback
        /// anchor because the row that fired this callback is inside the
        /// dismissed search flyout and no longer visible.
        /// </summary>
        private async void OnSearchResultActivated(Event evt, FrameworkElement anchor)
        {
            _searchResultsFlyout.Hide();

            var eventDay = DateHelpers.GetEventDayKey(evt.StartTimeUtc);
            _selectedDate = eventDay;
            _displayMonth = DateHelpers.GetMonthStartLocal(eventDay);
            SwitchView(CalendarView.Day);
            // SwitchView triggers RefreshActiveViewAsync; await one dispatcher
            // turn so the popover opens against a laid-out day.
            await Task.Yield();

            await EditEventAsync(evt, FallbackAnchor);
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

        // ── Agenda view rendering ─────────────────────────────────────────────

        private void RenderAgendaView()
        {
            var (startUtc, endUtc) = DateHelpers.GetAgendaRangeUtc(DateTime.Now);
            var startLocal = startUtc.ToLocalTime();
            var endLocal = endUtc.ToLocalTime();
            _agendaViewRenderer.Render(startLocal, endLocal, _eventsByDate, _allCalendars);
        }

        // ── Year view rendering ───────────────────────────────────────────────

        private void RenderYearView()
        {
            _yearViewRenderer.Render(_displayMonth.Year, _selectedDate, _eventsByDate);
        }

        /// <summary>
        /// Year View day-cell tap: drill from Year into Month at the tapped
        /// day. Sets both <see cref="_selectedDate"/> and
        /// <see cref="_displayMonth"/>, switches to Month view, and refreshes.
        /// Year always crosses the current loaded range for Month (Year loads
        /// the whole calendar year), so the switch always issues one DB pass —
        /// no clever short-circuit here.
        /// </summary>
        public async void OnYearDaySelected(DateTime date)
        {
            _selectedDate = DateHelpers.GetLocalDayKey(date);
            _displayMonth = DateHelpers.GetMonthStartLocal(_selectedDate);
            SwitchView(CalendarView.Month);
            await Task.Yield();
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
            // Remember the chip so EventPopover_EditRequested can hand it to
            // the edit popover — the editor reads as a continuation of the same
            // talk-bubble the read-only popover opened.
            _lastEventAnchor = anchor;
            var calendar = _allCalendars.FirstOrDefault(c => c.Id == evt.CalendarId);
            _eventPopover.SetEvent(evt, calendar);

            // Same Day-View carve-out as the edit/create popovers: full-width
            // Day chips force a centered placement instead of the right-edge
            // talk-bubble. Resolve per-show since the flyout is reused.
            var (resolvedAnchor, placement) = ResolvePopoverAnchor(anchor);
            _eventPopoverFlyout.Placement = placement;
            _eventPopoverFlyout.ShowAt(resolvedAnchor);
        }

        /// <summary>
        /// Selected-day panel row click: opens the edit popover directly,
        /// bypassing the read-only popover (a deliberate product distinction).
        /// Anchored to the clicked row so the popover reads as a talk-bubble
        /// from the row — same pattern as <see cref="OnEventClicked"/>.
        /// </summary>
        public async void OnEventActivated(Event evt, FrameworkElement anchor) =>
            await EditEventAsync(evt, anchor);

        private async void EventPopover_EditRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();
            await EditEventAsync(evt, _lastEventAnchor ?? FallbackAnchor);
        }

        private async void EventPopover_DeleteRequested(object? sender, Event evt)
        {
            _eventPopoverFlyout.Hide();

            try
            {
                if (evt.IsOccurrence)
                    await DeleteOccurrenceAsync(evt);
                else
                    await _eventRepository.DeleteAsync(evt.Id);

                await AfterDataMutationAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Error deleting event: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Recurring-event delete: presents a scope dialog (Skip this
        /// occurrence / Delete entire series). "Skip" loads the master,
        /// appends the occurrence's <c>SeriesAnchorUtc</c> to its EXDATE
        /// list, and writes the master back — a hole in the projection
        /// space, no occurrence ever persisted (per DECISIONS.md). "Delete
        /// series" falls through to the existing master-delete path.
        /// </summary>
        private async Task DeleteOccurrenceAsync(Event occurrence)
        {
            var dialog = new ContentDialog
            {
                Title = "Delete recurring event",
                Content = $"\"{occurrence.Title}\" is part of a recurring "
                       + "series. Skip just this occurrence, or delete the "
                       + "entire series?",
                PrimaryButtonText = "Skip this occurrence",
                SecondaryButtonText = "Delete entire series",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            switch (result)
            {
                case ContentDialogResult.Primary:
                    await AppendExDateAsync(occurrence);
                    break;

                case ContentDialogResult.Secondary:
                    await _eventRepository.DeleteAsync(occurrence.Id);
                    break;

                default:
                    return; // Cancelled — leave the working tree unchanged.
            }
        }

        private async Task AppendExDateAsync(Event occurrence)
        {
            if (occurrence.SeriesAnchorUtc is not DateTime anchor)
                return; // Should not happen — IsOccurrence guarantees non-null.

            var master = await _eventRepository.GetByIdAsync(occurrence.Id);
            if (master is null)
                return; // Master gone — refresh will drop the stale occurrence.

            var existing = master.RecurrenceExDatesUtc;
            var updated = new DateTime[existing.Count + 1];
            for (int i = 0; i < existing.Count; i++)
                updated[i] = existing[i];
            updated[existing.Count] = anchor;

            master.RecurrenceExDatesUtc = updated;
            master.UpdatedAtUtc = DateTime.UtcNow;

            // RecurrenceEndUtcCached is unaffected — EXDATE never changes
            // series termination (DECISIONS.md "Named invariants" #3, #6).
            await _eventRepository.UpdateAsync(master);
        }
    }
}
