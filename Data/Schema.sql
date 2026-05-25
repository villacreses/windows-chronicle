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

    RecurrenceRuleJson TEXT,

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