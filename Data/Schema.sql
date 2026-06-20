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
