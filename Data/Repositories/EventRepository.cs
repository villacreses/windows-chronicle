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
            RecurrenceRuleJson,
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
            $recurrenceRuleJson,
            $createdAtUtc,
            $updatedAtUtc
        );
        """;

        command.Parameters.AddWithValue(
            "$id",
            evt.Id.ToString());

        command.Parameters.AddWithValue(
            "$calendarId",
            evt.CalendarId.ToString());

        command.Parameters.AddWithValue(
            "$title",
            evt.Title);

        command.Parameters.AddWithValue(
            "$description",
            (object?)evt.Description ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$startTimeUtc",
            evt.StartTimeUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$endTimeUtc",
            evt.EndTimeUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$isAllDay",
            evt.IsAllDay ? 1 : 0);

        command.Parameters.AddWithValue(
            "$recurrenceRuleJson",
            (object?)evt.RecurrenceRuleJson ?? DBNull.Value);

        command.Parameters.AddWithValue(
            "$createdAtUtc",
            evt.CreatedAtUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            evt.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Event>> GetInRangeAsync(
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

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
            RecurrenceRuleJson,
            CreatedAtUtc,
            UpdatedAtUtc
        FROM Events
        WHERE
        (
            RecurrenceRuleJson IS NULL
            AND EndTimeUtc >= $rangeStartUtc
            AND StartTimeUtc <= $rangeEndUtc
        )
        OR
        (
            RecurrenceRuleJson IS NOT NULL
            AND StartTimeUtc <= $rangeEndUtc
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

                    RecurrenceRuleJson =
                        reader.IsDBNull(7)
                            ? null
                            : reader.GetString(7),

                    CreatedAtUtc =
                        ParseUtcDateTime(
                            reader.GetString(8)),

                    UpdatedAtUtc =
                        ParseUtcDateTime(
                            reader.GetString(9))
                });
        }

        return events;
    }
}
