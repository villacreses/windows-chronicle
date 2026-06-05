using Chronicle.Data.Repositories;
using Chronicle.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
            RenderDayHeaders();
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
        /// Builds the Calendars section in the sidebar from <see cref="_allCalendars"/>.
        /// Called once at startup. Call again if calendars are ever added/removed via UI.
        /// </summary>
        private void RenderSidebar()
        {
            SidebarPanel.Children.Clear();

            // Section header
            SidebarPanel.Children.Add(new TextBlock
            {
                Text = "Calendars",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 }),
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (_allCalendars.Count == 0)
            {
                SidebarPanel.Children.Add(new TextBlock
                {
                    Text = "No calendars yet.",
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(
                        new Windows.UI.Color { A = 180, R = 120, G = 120, B = 120 })
                });
                return;
            }

            foreach (var cal in _allCalendars)
            {
                var capturedId = cal.Id;

                // Color dot
                var colorDot = new Border
                {
                    Width = 12,
                    Height = 12,
                    CornerRadius = new CornerRadius(6),
                    Background = new SolidColorBrush(ParseHexColor(cal.Color)),
                    VerticalAlignment = VerticalAlignment.Center
                };

                // Calendar name
                var nameBlock = new TextBlock
                {
                    Text = cal.Name,
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                // Row content inside the CheckBox
                var rowContent = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalAlignment = VerticalAlignment.Center
                };
                rowContent.Children.Add(colorDot);
                rowContent.Children.Add(nameBlock);

                var checkBox = new CheckBox
                {
                    Content = rowContent,
                    IsChecked = _calendarVisibility.GetValueOrDefault(capturedId, true),
                    Padding = new Thickness(4, 6, 4, 6)
                };

                checkBox.Checked += async (s, e) =>
                {
                    _calendarVisibility[capturedId] = true;
                    await RefreshMonthAsync();
                };

                checkBox.Unchecked += async (s, e) =>
                {
                    _calendarVisibility[capturedId] = false;
                    await RefreshMonthAsync();
                };

                SidebarPanel.Children.Add(checkBox);
            }
        }

        // ── Date/time helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Gets the local calendar date key for event grouping and lookup.
        /// Event storage remains UTC; the month grid is a local wall-clock calendar.
        /// </summary>
        private static DateTime GetEventDayKey(DateTime utcDateTime)
        {
            return GetLocalDayKey(utcDateTime.ToLocalTime());
        }

        private static DateTime GetLocalDayKey(DateTime localDateTime)
        {
            return DateTime.SpecifyKind(localDateTime.Date, DateTimeKind.Local);
        }

        /// <summary>
        /// Gets the current month's start and end boundaries in UTC.
        /// Single source of truth for month range calculations.
        /// </summary>
        private (DateTime startUtc, DateTime endUtc) GetCurrentMonthRangeUtc()
        {
            var monthStartLocal = GetCurrentMonthStartLocal();
            var monthEndUtc = monthStartLocal.AddMonths(1).ToUniversalTime().AddTicks(-1);
            return (monthStartLocal.ToUniversalTime(), monthEndUtc);
        }

        private DateTime GetCurrentMonthStartLocal()
        {
            return new DateTime(
                _displayMonth.Year, _displayMonth.Month, 1, 0, 0, 0, DateTimeKind.Local);
        }

        private static DateTime CombineLocalDateAndTimeAsUtc(DateTime date, TimeSpan time)
        {
            var localDateTime =
                DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Local);

            return localDateTime.ToUniversalTime();
        }

        /// <summary>
        /// Parses a "#RRGGBB" hex color string into a <see cref="Windows.UI.Color"/>.
        /// Falls back to a neutral blue if the string is malformed.
        /// </summary>
        private static Windows.UI.Color ParseHexColor(string hex)
        {
            try
            {
                var s = hex.TrimStart('#');
                return new Windows.UI.Color
                {
                    A = 255,
                    R = Convert.ToByte(s[0..2], 16),
                    G = Convert.ToByte(s[2..4], 16),
                    B = Convert.ToByte(s[4..6], 16)
                };
            }
            catch
            {
                return new Windows.UI.Color { A = 255, R = 59, G = 130, B = 246 }; // #3B82F6
            }
        }

        // ── Event loading ─────────────────────────────────────────────────────

        private async Task LoadEventsAsync()
        {
            var (monthStart, monthEnd) = GetCurrentMonthRangeUtc();
            var events = await _eventRepository.GetInRangeAsync(monthStart, monthEnd);

            // Filter to calendars that are currently visible.
            // If _calendarVisibility is empty (no calendars), everything passes through.
            var visible = events
                .Where(e => _calendarVisibility.Count == 0
                            || _calendarVisibility.GetValueOrDefault(e.CalendarId, true));

            _eventsByDate = visible
                .GroupBy(e => GetEventDayKey(e.StartTimeUtc))
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        // ── Calendar grid rendering ───────────────────────────────────────────

        private void RenderDayHeaders()
        {
            DayNamesGrid.Children.Clear();

            var dayNames = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

            for (int i = 0; i < 7; i++)
            {
                var textBlock = new TextBlock
                {
                    Text = dayNames[i],
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(
                        new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 })
                };

                Grid.SetColumn(textBlock, i);
                DayNamesGrid.Children.Add(textBlock);
            }
        }

        private void RenderCalendarGrid()
        {
            CalendarGrid.Children.Clear();
            CalendarGrid.ColumnDefinitions.Clear();
            CalendarGrid.RowDefinitions.Clear();

            for (int i = 0; i < 7; i++)
                CalendarGrid.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var monthStart = GetCurrentMonthStartLocal();
            var firstDayOfWeek = (int)monthStart.DayOfWeek;
            var daysInMonth = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            var weeks = (int)Math.Ceiling((firstDayOfWeek + daysInMonth) / 7.0);

            for (int i = 0; i < weeks; i++)
                CalendarGrid.RowDefinitions.Add(
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            int cellIndex = 0;
            for (int week = 0; week < weeks; week++)
            {
                for (int day = 0; day < 7; day++)
                {
                    var cell = CreateDayCell(cellIndex, firstDayOfWeek, daysInMonth, monthStart);
                    Grid.SetRow(cell, week);
                    Grid.SetColumn(cell, day);
                    CalendarGrid.Children.Add(cell);
                    cellIndex++;
                }
            }
        }

        private Border CreateDayCell(
            int cellIndex, int firstDayOfWeek, int daysInMonth, DateTime monthStart)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(
                    new Windows.UI.Color { A = 200, R = 220, G = 220, B = 220 }),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 })
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8),
                Spacing = 4
            };

            bool isInMonth =
                cellIndex >= firstDayOfWeek && cellIndex < firstDayOfWeek + daysInMonth;

            if (isInMonth)
            {
                int dayNumber = cellIndex - firstDayOfWeek + 1;
                var dayDate = GetLocalDayKey(monthStart.AddDays(dayNumber - 1));

                stackPanel.Children.Add(new TextBlock
                {
                    Text = dayNumber.ToString(),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(
                        new Windows.UI.Color { A = 255, R = 0, G = 0, B = 0 })
                });

                if (_eventsByDate.TryGetValue(dayDate, out var events))
                    stackPanel.Children.Add(CreateEventList(events));

                border.PointerPressed += async (s, e) =>
                    await ShowCreateEventDialogAsync(dayDate);
            }
            else
            {
                border.Background = new SolidColorBrush(
                    new Windows.UI.Color { A = 255, R = 245, G = 245, B = 245 });
            }

            border.Child = stackPanel;
            return border;
        }

        // ── Event list inside a day cell ──────────────────────────────────────

        /// <summary>
        /// Creates a scrollable list of event chips for a single day cell.
        /// Each chip is clickable and opens the edit dialog; pointer events are
        /// marked Handled to prevent the day-cell create-dialog from also firing.
        /// </summary>
        private UIElement CreateEventList(List<Event> events)
        {
            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 100
            };

            var eventsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 2
            };

            foreach (var evt in events.Take(5))
            {
                var capturedEvt = evt;

                // Resolve the calendar color for this event (falls back to blue).
                var calColor = ResolveCalendarColor(capturedEvt.CalendarId);

                var eventTextBlock = new TextBlock
                {
                    Text = capturedEvt.Title,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(calColor),
                    Margin = new Thickness(0, 0, 0, 2)
                };

                var chip = new Border
                {
                    Child = eventTextBlock,
                    Background = new SolidColorBrush(new Windows.UI.Color { A = 0, R = 0, G = 0, B = 0 })
                };

                chip.PointerPressed += async (s, e) =>
                {
                    e.Handled = true;
                    await ShowEditEventDialogAsync(capturedEvt);
                };

                eventsPanel.Children.Add(chip);
            }

            if (events.Count > 5)
            {
                eventsPanel.Children.Add(new TextBlock
                {
                    Text = $"+{events.Count - 5} more",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(
                        new Windows.UI.Color { A = 200, R = 128, G = 128, B = 128 })
                });
            }

            scrollViewer.Content = eventsPanel;
            return scrollViewer;
        }

        /// <summary>
        /// Returns the <see cref="Windows.UI.Color"/> for the given calendar,
        /// using the cached <see cref="_allCalendars"/> list. Falls back to blue.
        /// </summary>
        private Windows.UI.Color ResolveCalendarColor(Guid calendarId)
        {
            var cal = _allCalendars.FirstOrDefault(c => c.Id == calendarId);
            return cal is not null
                ? ParseHexColor(cal.Color)
                : new Windows.UI.Color { A = 255, R = 33, G = 150, B = 243 };
        }

        // ── Shared dialog form ────────────────────────────────────────────────

        /// <summary>
        /// Builds the shared form panel used by both Create and Edit dialogs.
        /// Returns a getter delegate for the currently-selected calendar to avoid
        /// ref parameters (which cannot be captured inside lambdas).
        /// </summary>
        private (StackPanel panel,
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

        private async Task ShowCreateEventDialogAsync(DateTime selectedDay)
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
                        XamlRoot = this.Content.XamlRoot
                    }.ShowAsync();
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "Create Event",
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    XamlRoot = this.Content.XamlRoot
                };

                var (panel, titleBox, startPicker, endPicker, errorBlock, getCalendar) =
                    BuildEventForm(
                        calendars,
                        initialTitle: "",
                        initialStart: new TimeSpan(9, 0, 0),
                        initialEnd: new TimeSpan(10, 0, 0),
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
                            StartTimeUtc = CombineLocalDateAndTimeAsUtc(selectedDay, startPicker.Time),
                            EndTimeUtc = CombineLocalDateAndTimeAsUtc(selectedDay, endPicker.Time),
                            Description = null,
                            IsAllDay = false,
                            RecurrenceRuleJson = null,
                            CreatedAtUtc = nowUtc,
                            UpdatedAtUtc = nowUtc
                        };

                        newEvent.Validate();
                        await _eventRepository.InsertAsync(newEvent);
                        await RefreshMonthAsync();
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

        private async Task ShowEditEventDialogAsync(Event evt)
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
                    XamlRoot = this.Content.XamlRoot
                };

                int calendarIndex = Math.Max(0, calendars.FindIndex(c => c.Id == evt.CalendarId));

                var startLocal = evt.StartTimeUtc.ToLocalTime();
                var endLocal = evt.EndTimeUtc.ToLocalTime();
                var selectedDay = GetLocalDayKey(startLocal);

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
                        evt.StartTimeUtc = CombineLocalDateAndTimeAsUtc(selectedDay, startPicker.Time);
                        evt.EndTimeUtc = CombineLocalDateAndTimeAsUtc(selectedDay, endPicker.Time);
                        evt.UpdatedAtUtc = DateTime.UtcNow;

                        evt.Validate();
                        await _eventRepository.UpdateAsync(evt);
                        await RefreshMonthAsync();
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
                        await RefreshMonthAsync();
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
}
