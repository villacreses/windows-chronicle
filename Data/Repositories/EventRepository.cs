using Chronicle.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Chronicle.Data.Repositories;

public sealed class EventRepository
{
    private static DateTime ParseUtcDateTime(string value)
    {
        var parsed =
            DateTime.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);

        return DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);
    }

    // EXDATE is stored as newline-separated ISO-8601 UTC strings — no
    // JSON parser in the hot path, AOT-friendly.
    private const char ExDateSeparator = '\n';

    private static string? SerializeExDates(IReadOnlyList<DateTime> exDates)
    {
        if (exDates.Count == 0)
            return null;

        var parts = new string[exDates.Count];
        for (int i = 0; i < exDates.Count; i++)
            parts[i] = exDates[i].ToString("O");

        return string.Join(ExDateSeparator, parts);
    }

    private static IReadOnlyList<DateTime> ParseExDates(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return Array.Empty<DateTime>();

        var parts = value.Split(ExDateSeparator, StringSplitOptions.RemoveEmptyEntries);
        var result = new DateTime[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            result[i] = ParseUtcDateTime(parts[i]);

        return result;
    }

#pragma warning disable CA1822 // Mark members as static
    public async Task InsertAsync(Event evt)
#pragma warning restore CA1822 // Mark members as static
    {
        evt.Validate();

        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        INSERT INTO Events (
            Id,
            CalendarId,
            Title,
            Description,
            StartTimeUtc,
            EndTimeUtc,
            IsAllDay,
            RecurrenceRule,
            RecurrenceExDatesUtc,
            RecurrenceEndUtcCached,
            CreatedAtUtc,
            UpdatedAtUtc
        )
        VALUES (
            $id,
            $calendarId,
            $title,
            $description,
            $startTimeUtc,
            $endTimeUtc,
            $isAllDay,
            $recurrenceRule,
            $recurrenceExDates,
            $recurrenceEndUtcCached,
            $createdAtUtc,
            $updatedAtUtc
        );
        """;

        BindEventParameters(command, evt);

        command.Parameters.AddWithValue(
            "$createdAtUtc",
            evt.CreatedAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            evt.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateAsync(Event evt)
    {
        evt.Validate();

        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        UPDATE Events SET
            CalendarId             = $calendarId,
            Title                  = $title,
            Description            = $description,
            StartTimeUtc           = $startTimeUtc,
            EndTimeUtc             = $endTimeUtc,
            IsAllDay               = $isAllDay,
            RecurrenceRule         = $recurrenceRule,
            RecurrenceExDatesUtc   = $recurrenceExDates,
            RecurrenceEndUtcCached = $recurrenceEndUtcCached,
            UpdatedAtUtc           = $updatedAtUtc
        WHERE Id = $id;
        """;

        BindEventParameters(command, evt);

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            evt.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    private static void BindEventParameters(
        Microsoft.Data.Sqlite.SqliteCommand command,
        Event evt)
    {
        command.Parameters.AddWithValue("$id",           evt.Id.ToString());
        command.Parameters.AddWithValue("$calendarId",   evt.CalendarId.ToString());
        command.Parameters.AddWithValue("$title",        evt.Title);
        command.Parameters.AddWithValue("$description",  (object?)evt.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$startTimeUtc", evt.StartTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$endTimeUtc",   evt.EndTimeUtc.ToString("O"));
        command.Parameters.AddWithValue("$isAllDay",     evt.IsAllDay ? 1 : 0);
        command.Parameters.AddWithValue("$recurrenceRule",
            (object?)evt.RecurrenceRule ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurrenceExDates",
            (object?)SerializeExDates(evt.RecurrenceExDatesUtc) ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurrenceEndUtcCached",
            evt.RecurrenceEndUtcCached is DateTime end
                ? end.ToString("O")
                : (object)DBNull.Value);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            "DELETE FROM Events WHERE Id = $id;";

        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> CountByCalendarAsync(Guid calendarId)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
            "SELECT COUNT(*) FROM Events WHERE CalendarId = $id;";

        command.Parameters.AddWithValue("$id", calendarId.ToString());

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    public async Task<List<Event>> GetInRangeAsync(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        // Non-recurring branch: standard time-window overlap.
        // Recurring branch: master row qualifies if its series could
        // still produce occurrences inside the window — i.e. it started
        // on or before rangeEnd AND (the series hasn't ended yet OR its
        // cached end is at or past rangeStart). The actual expansion is
        // done in-memory after the query returns.
        command.CommandText =
        """
        SELECT
            Id,
            CalendarId,
            Title,
            Description,
            StartTimeUtc,
            EndTimeUtc,
            IsAllDay,
            RecurrenceRule,
            RecurrenceExDatesUtc,
            RecurrenceEndUtcCached,
            CreatedAtUtc,
            UpdatedAtUtc
        FROM Events
        WHERE
        (
            RecurrenceRule IS NULL
            AND EndTimeUtc >= $rangeStartUtc
            AND StartTimeUtc <= $rangeEndUtc
        )
        OR
        (
            RecurrenceRule IS NOT NULL
            AND StartTimeUtc <= $rangeEndUtc
            AND (
                RecurrenceEndUtcCached IS NULL
                OR RecurrenceEndUtcCached >= $rangeStartUtc
            )
        )
        ORDER BY StartTimeUtc;
        """;

        command.Parameters.AddWithValue(
            "$rangeStartUtc",
            rangeStartUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$rangeEndUtc",
            rangeEndUtc.ToString("O"));

        using var reader =
            await command.ExecuteReaderAsync();

        var events = new List<Event>();

        while (await reader.ReadAsync())
        {
            events.Add(
                new Event
                {
                    Id = Guid.Parse(
                        reader.GetString(0)),

                    CalendarId = Guid.Parse(
                        reader.GetString(1)),

                    Title = reader.GetString(2),

                    Description =
                        reader.IsDBNull(3)
                            ? null
                            : reader.GetString(3),

                    StartTimeUtc =
                        ParseUtcDateTime(
                            reader.GetString(4)),

                    EndTimeUtc =
                        ParseUtcDateTime(
                            reader.GetString(5)),

                    IsAllDay =
                        reader.GetInt32(6) == 1,

                    RecurrenceRule =
                        reader.IsDBNull(7)
                            ? null
                            : reader.GetString(7),

                    RecurrenceExDatesUtc =
                        ParseExDates(
                            reader.IsDBNull(8)
                                ? null
                                : reader.GetString(8)),

                    RecurrenceEndUtcCached =
                        reader.IsDBNull(9)
                            ? null
                            : ParseUtcDateTime(reader.GetString(9)),

                    CreatedAtUtc =
                        ParseUtcDateTime(
                            reader.GetString(10)),

                    UpdatedAtUtc =
                        ParseUtcDateTime(
                            reader.GetString(11))
                });
        }

        return events;
    }
}
