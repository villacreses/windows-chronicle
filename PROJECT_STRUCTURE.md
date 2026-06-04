# Chronicle Project Structure

## Overview
**Chronicle** is a lightweight Windows desktop calendar application built with .NET 8 and WinUI 3. It displays a month-view calendar grid backed by SQLite for event storage.

**Current Capabilities:**
- Display events in a traditional month-view calendar grid
- Load events from SQLite database
- Create new events via dialog UI
- Navigate between months with Previous/Next/Today buttons
- Support for multiple calendars with distinct colors
- All event times stored in UTC with timezone-aware conversion

**Status:** Active development on core UI and navigation workflows. Foundational database schema supports future features like recurring events and event editing, though these workflows are not yet fully implemented in the UI.

- **Framework**: .NET 8.0 (Windows 10.0.19041.0+)
- **UI Framework**: WinUI 3 (Windows App SDK 2.1.3)
- **Database**: SQLite (via Microsoft.Data.Sqlite 10.0.8)
- **Project Type**: Windows Desktop Application (WinExe)
- **Supported Platforms**: x86, x64, ARM64
- **Repository**: https://github.com/villacreses/windows-chronicle

---

## Directory Structure

```
Chronicle/
├── App.xaml                           # Application resources
├── App.xaml.cs                        # Application entry point & initialization
├── GlobalSuppressions.cs              # Global code analysis suppressions
├── app.manifest                       # Windows application manifest
├── Chronicle.csproj                   # Project file (.NET 8.0)
├── PROJECT_STRUCTURE.md               # This file - project documentation
│
├── Assets/                            # Application assets (icons, splash screen)
│   ├── SplashScreen.scale-200.png
│   ├── LockScreenLogo.scale-200.png
│   ├── Square150x150Logo.scale-200.png
│   ├── Square44x44Logo.scale-200.png
│   ├── Square44x44Logo.targetsize-24_altform-unplated.png
│   ├── StoreLogo.png
│   └── Wide310x150Logo.scale-200.png
│
├── Data/                              # Data access layer
│   ├── AppDatabase.cs                 # Database initialization & connection
│   ├── Schema.sql                     # SQLite database schema
│   └── Repositories/
│       ├── EventRepository.cs         # Event data access (CRUD operations)
│       └── CalendarRepository.cs      # Calendar data access (CRUD operations)
│
├── Models/                            # Domain models
│   ├── Calendar.cs                    # Calendar entity
│   │   ├── Id (Guid)
│   │   ├── Name (string)
│   │   └── Color (hex string, default: #3B82F6)
│   │
│   └── Event.cs                       # Event entity with validation
│       ├── Id (Guid)
│       ├── CalendarId (Guid)
│       ├── Title (string)
│       ├── Description (string?)
│       ├── StartTimeUtc (DateTime)
│       ├── EndTimeUtc (DateTime)
│       ├── IsAllDay (bool)
│       ├── RecurrenceRuleJson (string?)
│       ├── CreatedAtUtc (DateTime)
│       ├── UpdatedAtUtc (DateTime)
│       └── Validate() method
│
├── Views/                             # UI layer (WinUI 3)
│   ├── MainWindow.xaml                # Main window UI layout (XAML)
│   └── MainWindow.xaml.cs             # Main window code-behind (C#)
│
├── Properties/
│   ├── launchSettings.json            # Debug launch settings
│   └── PublishProfiles/
│       ├── win-arm64.pubxml           # ARM64 release profile
│       ├── win-x64.pubxml             # x64 release profile
│       └── win-x86.pubxml             # x86 release profile
│
├── Package.appxmanifest               # Windows app package manifest
├── .gitignore                         # Git ignore rules
└── .gitattributes                     # Git attributes

```

---

## Layer Architecture

### 1. **UI Layer** (`Views/`)
- **MainWindow**: Primary user interface for the calendar application
  - Built with WinUI 3 (XAML + C#)
  - Month-view calendar grid (7 columns, variable rows for weeks)
  - Month navigation controls (Previous, Next, Today buttons)
  - Dynamic month/year header display
  - Day-of-week headers (Sun–Sat)
  - Click-on-day creates event dialog
  - Events rendered as clickable text items within day cells

### 2. **Data Access Layer** (`Data/`)
- **AppDatabase**: 
  - Initializes SQLite database connection
  - Loads and executes schema from `Schema.sql`
  - Manages foreign key constraints
  - Database stored at: `{LocalAppData}/chronicle.db`
- **Repositories**: Repository pattern for data access
  - `EventRepository`: 
    - `InsertAsync()` - Create new events
    - `GetInRangeAsync()` - Query events by date range (used for month view)
    - Supports both one-time and recurring event queries (infrastructure present, UI not yet implemented)
  - `CalendarRepository`: Create/read calendars

### 3. **Models Layer** (`Models/`)
- **Calendar**: Represents a calendar entity
  - Properties: `Id` (Guid), `Name` (string), `Color` (hex string, default: #3B82F6)
  - Multiple calendars supported; calendars are displayed in event creation dialog
- **Event**: Represents a single event instance
  - Properties: `Id`, `CalendarId`, `Title`, `Description`, `StartTimeUtc`, `EndTimeUtc`, `IsAllDay`, `RecurrenceRuleJson`, `CreatedAtUtc`, `UpdatedAtUtc`
  - Validation: Enforces UTC kind and end-time ≥ start-time
  - All times stored and handled as UTC internally
  - `RecurrenceRuleJson` field present but not yet processed/expanded in UI

### Data Flow
```
User (UI interactions)
        ↓
    MainWindow
        ├─ On load: call RefreshMonthAsync()
        ├─ On month navigation: update _displayMonth, call RefreshMonthAsync()
        ├─ On day click: open Create Event dialog
        └─ On event save: InsertAsync(), call RefreshMonthAsync()
        ↓
EventRepository / CalendarRepository
        ├─ GetInRangeAsync(startUtc, endUtc) → fetch events for month
        └─ InsertAsync(event) → persist new event
        ↓
AppDatabase (SQLite connection pool)
        ↓
    SQLite database file (chronicle.db)
```

---

## Database Schema

The SQLite database includes two main tables:

### `Calendars` Table
```sql
CREATE TABLE IF NOT EXISTS Calendars (
	Id TEXT PRIMARY KEY,
	Name TEXT NOT NULL,
	Color TEXT NOT NULL
);
```

### `Events` Table
```sql
CREATE TABLE IF NOT EXISTS Events (
	Id TEXT PRIMARY KEY,
	CalendarId TEXT NOT NULL,
	Title TEXT NOT NULL,
	Description TEXT,
	StartTimeUtc TEXT NOT NULL,
	EndTimeUtc TEXT NOT NULL,
	IsAllDay INTEGER NOT NULL,
	RecurrenceRuleJson TEXT,
	CreatedAtUtc TEXT NOT NULL,
	UpdatedAtUtc TEXT NOT NULL,
	FOREIGN KEY (CalendarId) REFERENCES Calendars(Id)
);
```

### Indexes (for query optimization)
- `IX_Events_StartTimeUtc` - On `StartTimeUtc`
- `IX_Events_EndTimeUtc` - On `EndTimeUtc`
- `IX_Events_CalendarId` - On `CalendarId`

---

## Key Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| Microsoft.WindowsAppSDK | 2.1.3 | WinUI 3 framework for Windows desktop UI |
| Microsoft.Data.Sqlite | 10.0.8 | SQLite database access provider |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.1839 | Windows SDK build tools |

---

## Build Configuration

### Project Settings
- **Target Framework**: net8.0-windows10.0.19041.0
- **Minimum Platform Version**: 10.0.17763.0 (Windows 10)
- **Nullable Reference Types**: Enabled (strict null safety)
- **Output Type**: WinExe (Windows desktop application)
- **Runtime Identifiers**: win-x86, win-x64, win-arm64
- **MSIX Packaging**: Enabled for Windows App packaging

### Publish Configuration
- **ReadyToRun**: False (Debug), True (Release)
  - Compile intermediate language to native code ahead-of-time
- **Trimmed**: False (Debug), True (Release)
  - Remove unused code for smaller deployments

### Publish Profiles
Three platform-specific publish profiles available:
- `win-x86.pubxml` - x86 32-bit
- `win-x64.pubxml` - x64 64-bit  
- `win-arm64.pubxml` - ARM64 (Windows on ARM devices)

---

## Data Flow

```
User (UI)
	↓
MainWindow (Views)
	↓
Repositories (EventRepository, CalendarRepository)
	↓
AppDatabase (SQLite Connection)
	↓
sqlite database file (chronicle.db)
```

---

## File Storage

- **Database File**: Stored in `{LocalAppData}/chronicle.db`
  - Uses Windows.Storage.ApplicationData.Current.LocalFolder
  - Isolated per user and application
- **Schema File**: Embedded in project and copied to output directory at build time
  - Located at: `Data/Schema.sql`
  - Build action: `Content` with `CopyToOutputDirectory: PreserveNewest`

---

## Current UI Layout

The main calendar view consists of:

1. **Navigation Header** (top)
   - Previous button (navigate to previous month)
   - Today button (return to current month)
   - Next button (navigate to next month)
   - Month/Year display (e.g., "June 2026", updated dynamically)

2. **Day-of-Week Headers** (row below navigation)
   - Sun, Mon, Tue, Wed, Thu, Fri, Sat

3. **Calendar Grid** (main area)
   - 7 columns (one per day of week)
   - Variable rows (typically 4–6 weeks per month)
   - Each cell represents a single day
   - Days from current month are interactive (white background); adjacent month days are muted (light gray)
   - Click any day in the current month to create a new event
   - Events listed as text items within day cells (up to 5 visible; "+N more" indicator if overflow)

4. **Event Creation Dialog** (modal)
   - Triggered when user clicks a day
   - Fields: Event title (required), Calendar selection (dropdown or label), Start time picker, End time picker
   - Validation: Title required, end time ≥ start time
   - Saves event and refreshes calendar view

---

## Current Capabilities

**Calendar Navigation:**
- Display month-view grid
- Navigate to previous month
- Navigate to next month
- Jump to current month ("Today" button)
- Display month/year header with automatic updates

**Event Management:**
- Create new events via dialog
- Specify event title
- Specify start and end times (time pickers)
- Assign event to a calendar (from dropdown)
- Save event to SQLite database
- Query events by month date range

**Data Persistence:**
- Multiple calendars stored in SQLite
- Multiple events per calendar
- Event metadata: title, description, times (UTC), all-day flag
- Created/updated timestamps
- Color-coded calendars

**UI Rendering:**
- Month-view calendar grid
- Dynamic day cells with borders and spacing
- Event text display within day cells
- Scrollable event lists (max height per cell)
- Responsive grid layout

---

## Known Limitations

- **No event editing**: Once created, events cannot be modified via UI
- **No event deletion**: Events cannot be deleted via UI
- **No recurring event UI**: The database supports recurrence rules (JSON), but the UI does not provide:
  - UI to create recurring events
  - Expansion/rendering of recurring event instances
  - Handling of exception dates or recurrence parameters
- **No external integrations**: No Google Calendar, Outlook Calendar, or other provider sync
- **Month view only**: No day view, week view, or agenda view
- **Limited styling**: Minimal visual customization (colors, fonts, themes)
- **No search**: No event search or filtering
- **No multi-day event support**: Events assumed to be single-day or span visible via start/end time only
- **No reminders/notifications**: No alert system

---

## Next Milestones

Likely candidates for future implementation:

1. **Event Editing** - Modify existing event details (title, time, calendar)
2. **Event Deletion** - Remove events with confirmation dialog
3. **Recurring Events UI** - Create and expand recurring events in calendar view
4. **Week View** - Alternative layout showing 7-day grid with hourly slots
5. **External Calendar Sync** - Integrate with Google Calendar, Outlook, or iCalendar feeds
6. **Event Details Panel** - View/edit full event details including description
7. **Search/Filter** - Find events by keyword or date range
8. **Drag-and-Reschedule** - Move events between days or adjust times via drag-drop
9. **Themes/Customization** - Light/dark mode, calendar color themes
10. **Notifications** - Event reminders at configurable intervals

---

## Development Notes

- **Code-Behind Pattern**: UI logic implemented in MainWindow.xaml.cs without MVVM framework (intentionally simple for early-stage development)
- **Async/Await**: All database operations use async Task pattern for non-blocking UI
- **Nullable Reference Types**: Enabled throughout the project for null-safety
- **XAML Code Generation**: Disabled (manual code-behind implementation)
- **Time Handling**: All times stored as UTC; local time converted at display/input boundaries
- **Month Navigation Invariant**: `_displayMonth` always represents the first day of displayed month (e.g., 2026-06-01, never 2026-06-17)
- **Centralized Refresh**: `RefreshMonthAsync()` is the single entry point for calendar updates (load events, render grid, update header)
- **Repository Pattern**: Data access abstraction layer for maintainability and testability
- **Database Schema**: Automatically loaded and initialized on application startup from `Schema.sql`
- **Git Repository**: Primary branch is `main` with remote at `origin`

### Month Navigation Flow
```
User clicks [Previous/Next/Today] button
        ↓
Event handler updates _displayMonth
        ↓
Calls RefreshMonthAsync()
        ↓
LoadEventsAsync() → queries EventRepository.GetInRangeAsync()
        ↓
RenderDayHeaders() → renders day names (Sun–Sat)
        ↓
RenderCalendarGrid() → creates grid cells, wires click handlers, renders events
        ↓
UpdateMonthYearHeader() → updates TextBlock to display month/year
        ↓
UI updated
```

### Event Creation Flow
```
User clicks day cell
        ↓
ShowCreateEventDialogAsync() opens dialog
        ↓
User fills title, selects calendar, picks times
        ↓
User clicks "Save"
        ↓
Validate event (title required, end ≥ start)
        ↓
EventRepository.InsertAsync() persists to SQLite
        ↓
RefreshMonthAsync() reloads and rerenders calendar
        ↓
Dialog closes, calendar updated with new event
```

