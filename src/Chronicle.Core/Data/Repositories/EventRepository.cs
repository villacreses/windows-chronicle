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
        RefuseOccurrence(evt);

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
            TimeZoneId,
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
            $timeZoneId,
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
        RefuseOccurrence(evt);

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
            TimeZoneId             = $timeZoneId,
            UpdatedAtUtc           = $updatedAtUtc
        WHERE Id = $id;
        """;

        BindEventParameters(command, evt);

        command.Parameters.AddWithValue(
            "$updatedAtUtc",
            evt.UpdatedAtUtc.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    // Chokepoint enforcement of the persistence invariant: only master /
    // standalone rows are persisted. Expanded occurrences are projections
    // and must not reach the writer — see DECISIONS.md "Recurrence ...
    // named invariants" #1. Persisting an occurrence would overwrite the
    // master with the occurrence's projected times, silently destroying
    // the series; this guard fails loudly at the boundary instead.
    private static void RefuseOccurrence(Event evt)
    {
        if (evt.IsOccurrence)
        {
            throw new InvalidOperationException(
                "Cannot persist an expanded occurrence. The repository writes "
                + "persistent rows only; occurrences are projections. Use the "
                + "EXDATE write path to skip an occurrence, or update the "
                + "master row to change the series.");
        }
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
        command.Parameters.AddWithValue("$timeZoneId",
            (object?)evt.TimeZoneId ?? DBNull.Value);
        command.Parameters.AddWithValue("$recurrenceEndUtcCached",
            evt.RecurrenceEndUtcCached is DateTime end
                ? end.ToString("O")
                : (object)DBNull.Value);
    }

    /// <summary>
    /// Deletes an event and all of its <c>EventOverride</c> rows in a
    /// single transaction. Override-delete runs first so the FK on
    /// EventOverrides.SeriesEventId is never violated. Mirrors the
    /// cascade pattern <see cref="CalendarRepository.DeleteAsync"/> uses
    /// for Events → Calendars (see DECISIONS.md for why cascade lives in
    /// the repository, not in a schema ON DELETE CASCADE).
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        using var connection = AppDatabase.GetConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            await OverrideRepository.DeleteForSeriesInTransactionAsync(
                connection, transaction, id);

            using (var deleteEvent = connection.CreateCommand())
            {
                deleteEvent.Transaction = transaction;
                deleteEvent.CommandText =
                    "DELETE FROM Events WHERE Id = $id;";
                deleteEvent.Parameters.AddWithValue("$id", id.ToString());
                await deleteEvent.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
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

    /// <summary>
    /// Fetches a single persisted row by Id, or null if missing. Used by
    /// edit / skip-occurrence flows that hold a projected occurrence and
    /// need to reach its master row.
    /// </summary>
    public async Task<Event?> GetByIdAsync(Guid id)
    {
        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

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
            TimeZoneId,
            CreatedAtUtc,
            UpdatedAtUtc
        FROM Events
        WHERE Id = $id
        LIMIT 1;
        """;

        command.Parameters.AddWithValue("$id", id.ToString());

        using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return null;

        return ReadEvent(reader);
    }

    private static Event ReadEvent(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        return new Event
        {
            Id = Guid.Parse(reader.GetString(0)),
            CalendarId = Guid.Parse(reader.GetString(1)),
            Title = reader.GetString(2),
            Description = reader.IsDBNull(3) ? null : reader.GetString(3),
            StartTimeUtc = ParseUtcDateTime(reader.GetString(4)),
            EndTimeUtc = ParseUtcDateTime(reader.GetString(5)),
            IsAllDay = reader.GetInt32(6) == 1,
            RecurrenceRule = reader.IsDBNull(7) ? null : reader.GetString(7),
            RecurrenceExDatesUtc = ParseExDates(
                reader.IsDBNull(8) ? null : reader.GetString(8)),
            RecurrenceEndUtcCached = reader.IsDBNull(9)
                ? null
                : ParseUtcDateTime(reader.GetString(9)),
            TimeZoneId = reader.IsDBNull(10) ? null : reader.GetString(10),
            CreatedAtUtc = ParseUtcDateTime(reader.GetString(11)),
            UpdatedAtUtc = ParseUtcDateTime(reader.GetString(12)),
        };
    }

    /// <summary>
    /// Returns candidate rows for a text search over Title / Description
    /// within a UTC window. The result is the input to
    /// <c>EventProjection.SearchOccurrences</c>, which expands recurring
    /// masters into occurrences and re-filters each merged occurrence's
    /// Title / Description against the same query — because an override
    /// can flip either field either direction and the DB-level match
    /// works on stored strings, not merged ones.
    ///
    /// The candidate set is the union of:
    /// <list type="bullet">
    ///   <item>rows whose stored Title or Description matches (standalone
    ///   or master), and</item>
    ///   <item>recurring masters whose <c>EventOverrides</c> carry a
    ///   Title or Description match — the master itself may not match
    ///   but one of its occurrences might. This union at the SQL layer
    ///   is what closes the "override-only match" gap the load pipeline
    ///   tolerates in the non-search path (see RECURRENCE.md,
    ///   "Master-loading gap for cross-boundary overrides").</item>
    /// </list>
    ///
    /// <para>An empty or whitespace <paramref name="query"/> returns an
    /// empty list; the repository never treats it as "match everything."
    /// </para>
    ///
    /// The window filter matches <see cref="GetInRangeAsync"/>: finite
    /// series are pruned by <c>RecurrenceEndUtcCached</c> (advisory),
    /// standalone rows by the standard overlap check. Range refinement
    /// on merged occurrence start/end happens at the projection layer.
    /// </summary>
    public async Task<List<Event>> SearchCandidatesAsync(
        string query,
        DateTime rangeStartUtc,
        DateTime rangeEndUtc)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<Event>();

        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            e.Id,
            e.CalendarId,
            e.Title,
            e.Description,
            e.StartTimeUtc,
            e.EndTimeUtc,
            e.IsAllDay,
            e.RecurrenceRule,
            e.RecurrenceExDatesUtc,
            e.RecurrenceEndUtcCached,
            e.TimeZoneId,
            e.CreatedAtUtc,
            e.UpdatedAtUtc
        FROM Events e
        WHERE
        (
            (e.RecurrenceRule IS NULL
             AND e.EndTimeUtc >= $rangeStartUtc
             AND e.StartTimeUtc <= $rangeEndUtc)
            OR
            (e.RecurrenceRule IS NOT NULL
             AND e.StartTimeUtc <= $rangeEndUtc
             AND (e.RecurrenceEndUtcCached IS NULL
                  OR e.RecurrenceEndUtcCached >= $rangeStartUtc))
        )
        AND (
            e.Title LIKE $like ESCAPE '\'
            OR e.Description LIKE $like ESCAPE '\'
            OR (e.RecurrenceRule IS NOT NULL AND EXISTS (
                SELECT 1 FROM EventOverrides o
                WHERE o.SeriesEventId = e.Id
                AND (o.Title LIKE $like ESCAPE '\'
                     OR o.Description LIKE $like ESCAPE '\')
            ))
        )
        ORDER BY e.StartTimeUtc;
        """;

        command.Parameters.AddWithValue(
            "$rangeStartUtc",
            rangeStartUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$rangeEndUtc",
            rangeEndUtc.ToString("O"));

        command.Parameters.AddWithValue(
            "$like",
            BuildLikePattern(query));

        using var reader = await command.ExecuteReaderAsync();

        var events = new List<Event>();
        while (await reader.ReadAsync())
            events.Add(ReadEvent(reader));

        return events;
    }

    // Escape SQL LIKE metacharacters so a query like "50%" doesn't
    // become a wildcard match. The backslash is declared as the
    // ESCAPE character in the SQL text.
    private static string BuildLikePattern(string query)
    {
        var escaped = query
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return "%" + escaped + "%";
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
            TimeZoneId,
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

        using var reader = await command.ExecuteReaderAsync();

        var events = new List<Event>();
        while (await reader.ReadAsync())
            events.Add(ReadEvent(reader));

        return events;
    }
}
