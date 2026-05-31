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
        private DateTime _currentMonth;
        private Dictionary<DateTime, List<Event>> _eventsByDate = new();

        public MainWindow()
        {
            InitializeComponent();
            _currentMonth = DateTime.UtcNow;

            // Initialize calendar after window is fully loaded
            DispatcherQueue.TryEnqueue(async () => await InitializeCalendarAsync());
        }

        private async Task InitializeCalendarAsync()
        {
            try
            {
                await LoadEventsAsync();
                RenderCalendarGrid();
                RenderDayHeaders();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing calendar: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Gets the UTC date key for event grouping.
        /// Extracts only the date portion (time set to midnight) from a UTC DateTime.
        /// This method centralizes the event-to-day mapping logic for future extensibility.
        /// </summary>
        private static DateTime GetEventDayKey(DateTime utc)
        {
            return utc.Date;
        }

        /// <summary>
        /// Gets the current month's start and end boundaries in UTC.
        /// Single source of truth for month range calculations used across calendar rendering and event loading.
        /// </summary>
        private (DateTime startUtc, DateTime endUtc) GetCurrentMonthRangeUtc()
        {
            var monthStart = new DateTime(_currentMonth.Year, _currentMonth.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1).AddTicks(-1);
            return (monthStart, monthEnd);
        }

        private async Task LoadEventsAsync()
        {
            var (monthStart, monthEnd) = GetCurrentMonthRangeUtc();

            // Fetch events for the month
            var events = await _eventRepository.GetInRangeAsync(monthStart, monthEnd);

            // Group events by date using explicit day key extraction
            _eventsByDate = events
                .GroupBy(e => GetEventDayKey(e.StartTimeUtc))
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        private async Task RenderDayHeaders()
        {
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
                    Foreground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 100, G = 100, B = 100 })
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

            // Setup column definitions (7 columns for days of week)
            for (int i = 0; i < 7; i++)
            {
                CalendarGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }

            // Calculate grid layout using centralized month range
            var (monthStart, _) = GetCurrentMonthRangeUtc();
            var monthEnd = monthStart.AddMonths(1).AddDays(-1);
            var firstDayOfWeek = (int)monthStart.DayOfWeek;
            var daysInMonth = monthEnd.Day;
            var totalCells = firstDayOfWeek + daysInMonth;
            var weeks = (int)Math.Ceiling(totalCells / 7.0);

            // Setup row definitions
            for (int i = 0; i < weeks; i++)
            {
                CalendarGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            // Populate calendar cells
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

        private Border CreateDayCell(int cellIndex, int firstDayOfWeek, int daysInMonth, DateTime monthStart)
        {
            var border = new Border
            {
                BorderBrush = new SolidColorBrush(new Windows.UI.Color { A = 200, R = 220, G = 220, B = 220 }),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2),
                Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 })
            };

            var stackPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(8),
                Spacing = 4
            };

            // Determine if this cell is in the current month
            bool isInMonth = cellIndex >= firstDayOfWeek && cellIndex < firstDayOfWeek + daysInMonth;

            if (isInMonth)
            {
                int dayNumber = cellIndex - firstDayOfWeek + 1;
                var dayDate = monthStart.AddDays(dayNumber - 1);

                // Day number header
                var dayTextBlock = new TextBlock
                {
                    Text = dayNumber.ToString(),
                    FontSize = 16,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 0, G = 0, B = 0 })
                };
                stackPanel.Children.Add(dayTextBlock);

                // Add events for this day
                if (_eventsByDate.TryGetValue(dayDate, out var events))
                {
                    stackPanel.Children.Add(CreateEventList(events));
                }
            }
            else
            {
                // Empty cell (previous/next month)
                border.Background = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 245, G = 245, B = 245 });
            }

            border.Child = stackPanel;
            return border;
        }

        /// <summary>
        /// Creates a scrollable UI element containing the list of events for a day.
        /// Encapsulates event rendering logic: text formatting, overflow handling, and layout.
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
                var eventTextBlock = new TextBlock
                {
                    Text = evt.Title,
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(new Windows.UI.Color { A = 255, R = 33, G = 150, B = 243 }),
                    Margin = new Thickness(0, 0, 0, 2)
                };
                eventsPanel.Children.Add(eventTextBlock);
            }

            if (events.Count > 5)
            {
                var moreTextBlock = new TextBlock
                {
                    Text = $"+{events.Count - 5} more",
                    FontSize = 10,
                    Foreground = new SolidColorBrush(new Windows.UI.Color { A = 200, R = 128, G = 128, B = 128 })
                };
                eventsPanel.Children.Add(moreTextBlock);
            }

            scrollViewer.Content = eventsPanel;
            return scrollViewer;
        }
    }
}
