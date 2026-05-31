# Chronicle Project Structure

## Overview
**Chronicle** is a Windows desktop application built with .NET 8 and WinUI 3. It's a calendar/event management system that stores data in SQLite and allows users to manage calendars and events with recurring event support.

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
- Displays calendars and events with an interactive UI

### 2. **Data Access Layer** (`Data/`)
- **AppDatabase**: 
  - Initializes SQLite database connection
  - Loads and executes schema from `Schema.sql`
  - Manages foreign key constraints
  - Database stored at: `{LocalAppData}/chronicle.db`
- **Repositories**: Repository pattern for data access
  - `EventRepository`: CRUD operations and queries for events
  - `CalendarRepository`: CRUD operations and queries for calendars

### 3. **Models Layer** (`Models/`)
- **Calendar**: Represents a calendar entity
  - Properties: `Id` (Guid), `Name` (string), `Color` (hex string)
- **Event**: Represents an event with full validation
  - Properties: `Id`, `CalendarId`, `Title`, `Description`, timestamps, recurrence rule
  - Includes `Validate()` method to ensure end time ≥ start time
  - Support for all-day events and recurring events (via JSON recurrence rule)
  - All times stored as UTC

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

## Core Features

1. **Calendar Management**: Create and manage multiple calendars with distinct colors
2. **Event Management**: Create, read, update, delete events with full CRUD support
3. **Recurring Events**: Support for recurring events (recurrence rule stored as JSON)
4. **All-Day Events**: Dedicated flag for all-day event support
5. **Event Queries**: Query events within date ranges (using indexed StartTimeUtc/EndTimeUtc)
6. **Data Validation**: Event validation ensures logical event times
7. **Timestamps**: Track created and updated timestamps in UTC

---

## Development Notes

- **Nullable Reference Types**: Enabled throughout the project for null-safety
- **XAML Code Generation**: Disabled (manual code-behind implementation)
- **Code Generation**: Program generation disabled (custom entry point handling)
- **Database Schema**: Automatically loaded and initialized on application startup
- **Repository Pattern**: Data access abstraction layer for maintainability
- **Time Handling**: All times stored and handled in UTC internally
- **Git Repository**: Primary branch is `main` with remote at `origin`

