CREATE TABLE IF NOT EXISTS Calendars (
    Id TEXT PRIMARY KEY,
    Name TEXT NOT NULL,
    Color TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Events (
    Id TEXT PRIMARY KEY,

    CalendarId TEXT NOT NULL,

    Title TEXT NOT NULL,
    Description TEXT,

    StartTimeUtc TEXT NOT NULL,
    EndTimeUtc TEXT NOT NULL,

    IsAllDay INTEGER NOT NULL,

    RecurrenceRule TEXT,
    RecurrenceExDatesUtc TEXT,
    RecurrenceEndUtcCached TEXT,
    TimeZoneId TEXT,

    CreatedAtUtc TEXT NOT NULL,
    UpdatedAtUtc TEXT NOT NULL,

    FOREIGN KEY (CalendarId) REFERENCES Calendars(Id)
);

CREATE INDEX IF NOT EXISTS IX_Events_StartTimeUtc
ON Events(StartTimeUtc);

CREATE INDEX IF NOT EXISTS IX_Events_EndTimeUtc
ON Events(EndTimeUtc);

CREATE INDEX IF NOT EXISTS IX_Events_CalendarId
ON Events(CalendarId);

-- IX_Events_RecurrenceEndUtcCached is created by AppDatabase
-- .MigrateRecurrenceColumns so it lands on both fresh installs (after
-- the column exists from CREATE TABLE) and migrated databases (after
-- ALTER TABLE adds the column). It cannot live here because this file
-- runs before the migration step, and on a pre-recurrence DB the
-- column does not yet exist.

CREATE TABLE IF NOT EXISTS EventOverrides (
    Id TEXT PRIMARY KEY,

    SeriesEventId TEXT NOT NULL,
    OccurrenceAnchorUtc TEXT NOT NULL,

    -- Override fields (nullable = inherit from master).
    Title TEXT,
    Description TEXT,
    StartTimeUtc TEXT,
    EndTimeUtc TEXT,
    IsAllDay INTEGER,

    UpdatedAtUtc TEXT NOT NULL,

    UNIQUE(SeriesEventId, OccurrenceAnchorUtc),
    FOREIGN KEY (SeriesEventId) REFERENCES Events(Id)
);

CREATE INDEX IF NOT EXISTS IX_EventOverrides_SeriesEventId
ON EventOverrides(SeriesEventId);

CREATE INDEX IF NOT EXISTS IX_EventOverrides_OccurrenceAnchorUtc
ON EventOverrides(OccurrenceAnchorUtc);

-- Reminders are composed children of the Event aggregate (like
-- EventOverrides): cascade-deleted with their event via the repository
-- transactions, never referenced from outside the aggregate. The offset
-- is stored as the user expressed it — (Quantity, Unit), never a
-- normalized minute count. Units are fixed durations only
-- ('Minutes' | 'Hours' | 'Days' | 'Weeks'); see REMINDERS.md.
CREATE TABLE IF NOT EXISTS Reminders (
    Id TEXT PRIMARY KEY,

    EventId TEXT NOT NULL,

    OffsetQuantity INTEGER NOT NULL,
    OffsetUnit TEXT NOT NULL,

    FOREIGN KEY (EventId) REFERENCES Events(Id)
);

CREATE INDEX IF NOT EXISTS IX_Reminders_EventId
ON Reminders(EventId);
