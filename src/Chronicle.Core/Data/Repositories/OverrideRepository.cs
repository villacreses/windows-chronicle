using Chronicle.Models.Recurrence;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Chronicle.Data.Repositories;

/// <summary>
/// Persistence for <see cref="EventOverride"/> rows — per-occurrence
/// divergences in a recurring series. See DECISIONS.md "Recurrence ...
/// Phase 2" for the model semantics.
///
/// Sub-step 1 API uses primitive identifiers because the typed
/// <c>EventRef</c> primitive arrives in sub-step 3 alongside its first
/// non-test caller; introducing it here ahead of use would violate the
/// "abstractions at point of use" guideline.
/// </summary>
public sealed class OverrideRepository
{
    private static DateTime ParseUtcDateTime(string value)
    {
        var parsed = DateTime.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        return DateTime.SpecifyKind(parsed.ToUniversalTime(), DateTimeKind.Utc);
    }

    /// <summary>
    /// Inserts or updates the override for
    /// <c>(target.SeriesId, target.AnchorUtc)</c>. Conflict resolution
    /// is row-level (SQLite ON CONFLICT DO UPDATE), so the row's
    /// <c>Id</c> is preserved across updates — only the override fields
    /// and <c>UpdatedAtUtc</c> change.
    ///
    /// The target's <c>AnchorUtc</c> must equal a walker-emitted anchor
    /// of the series' rule bit-for-bit; the UI write path threads
    /// <c>SeriesAnchorUtc</c> through <see cref="EventRef.From"/>
    /// verbatim, per DECISIONS.md "Named invariants" #3.
    ///
    /// The signature takes <see cref="EventRef.Occurrence"/> rather than
    /// raw <c>(Guid, DateTime)</c> so the master-variant call is a
    /// compile error rather than a runtime branch. This is the Phase 2A
    /// landing of the wrapper-tripwire deal recorded in DECISIONS.md.
    /// </summary>
#pragma warning disable CA1822 // Mark members as static
    public async Task UpsertAsync(EventRef.Occurrence target, OverrideFields fields)
#pragma warning restore CA1822 // Mark members as static
    {
        ValidateFields(target, fields);

        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        INSERT INTO EventOverrides (
            Id,
            SeriesEventId,
            OccurrenceAnchorUtc,
            Title,
            Description,
            StartTimeUtc,
            EndTimeUtc,
            IsAllDay,
            UpdatedAtUtc
        )
        VALUES (
            $id,
            $seriesEventId,
            $anchorUtc,
            $title,
            $description,
            $startTimeUtc,
            $endTimeUtc,
            $isAllDay,
            $updatedAtUtc
        )
        ON CONFLICT (SeriesEventId, OccurrenceAnchorUtc) DO UPDATE SET
            Title         = excluded.Title,
            Description   = excluded.Description,
            StartTimeUtc  = excluded.StartTimeUtc,
            EndTimeUtc    = excluded.EndTimeUtc,
            IsAllDay      = excluded.IsAllDay,
            UpdatedAtUtc  = excluded.UpdatedAtUtc;
        """;

        // Id is generated for the insert case; on conflict the existing
        // row's Id is preserved by ON CONFLICT DO UPDATE semantics.
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$seriesEventId", target.SeriesId.ToString());
        command.Parameters.AddWithValue("$anchorUtc", target.AnchorUtc.ToString("O"));
        command.Parameters.AddWithValue("$title", (object?)fields.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$description", (object?)fields.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$startTimeUtc",
            fields.StartTimeUtc is DateTime s ? s.ToString("O") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$endTimeUtc",
            fields.EndTimeUtc is DateTime e ? e.ToString("O") : (object)DBNull.Value);
        command.Parameters.AddWithValue("$isAllDay",
            fields.IsAllDay is bool b ? (b ? 1 : 0) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$updatedAtUtc", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    private static void ValidateFields(EventRef.Occurrence target, OverrideFields fields)
    {
        if (target.AnchorUtc.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                "EventRef.Occurrence.AnchorUtc must be UTC.");

        if (fields.StartTimeUtc is DateTime s && s.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                "OverrideFields.StartTimeUtc must be UTC when set.");

        if (fields.EndTimeUtc is DateTime e && e.Kind != DateTimeKind.Utc)
            throw new InvalidOperationException(
                "OverrideFields.EndTimeUtc must be UTC when set.");

        if (fields.StartTimeUtc is DateTime ss
            && fields.EndTimeUtc is DateTime ee
            && ee < ss)
        {
            throw new InvalidOperationException(
                "Override end time cannot be before override start time.");
        }
    }

    /// <summary>
    /// Returns every override for the given series. The expander caller
    /// filters per-master, so an unscoped fetch (no anchor-range filter)
    /// is the most useful shape — orphaned-anchor checks happen at
    /// expansion time, not in SQL.
    /// </summary>
    public async Task<List<EventOverride>> GetForSeriesAsync(Guid seriesEventId)
    {
        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            Id,
            SeriesEventId,
            OccurrenceAnchorUtc,
            Title,
            Description,
            StartTimeUtc,
            EndTimeUtc,
            IsAllDay,
            UpdatedAtUtc
        FROM EventOverrides
        WHERE SeriesEventId = $seriesEventId
        ORDER BY OccurrenceAnchorUtc;
        """;

        command.Parameters.AddWithValue("$seriesEventId", seriesEventId.ToString());

        using var reader = await command.ExecuteReaderAsync();

        var result = new List<EventOverride>();
        while (await reader.ReadAsync())
            result.Add(ReadOverride(reader));

        return result;
    }

    /// <summary>
    /// Bulk fetch of overrides for any of the given series, in a single
    /// statement. Used by the load pipeline so the expander never issues
    /// per-master queries — the caller groups by <c>SeriesEventId</c>
    /// and passes the per-series slice into <c>Expand</c>.
    ///
    /// Filters by series id rather than anchor range deliberately. An
    /// override could carry an anchor outside the load range but a
    /// merged Start inside it (the user moved an occurrence across the
    /// boundary). Filtering by anchor range would drop those silently;
    /// filtering by series keeps them in the slice and lets the
    /// expander's range check (on merged Start/End) decide.
    ///
    /// Empty input returns an empty list — avoids constructing an
    /// invalid <c>IN ()</c> clause.
    /// </summary>
    public async Task<List<EventOverride>> GetForSeriesAsync(
        IReadOnlyList<Guid> seriesEventIds)
    {
        if (seriesEventIds.Count == 0)
            return new List<EventOverride>();

        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

        var paramNames = new string[seriesEventIds.Count];
        for (int i = 0; i < seriesEventIds.Count; i++)
            paramNames[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);

        command.CommandText =
            "SELECT "
            + "Id, SeriesEventId, OccurrenceAnchorUtc, "
            + "Title, Description, StartTimeUtc, EndTimeUtc, IsAllDay, "
            + "UpdatedAtUtc "
            + "FROM EventOverrides "
            + "WHERE SeriesEventId IN (" + string.Join(", ", paramNames) + ") "
            + "ORDER BY SeriesEventId, OccurrenceAnchorUtc;";

        for (int i = 0; i < seriesEventIds.Count; i++)
            command.Parameters.AddWithValue(paramNames[i], seriesEventIds[i].ToString());

        using var reader = await command.ExecuteReaderAsync();

        var result = new List<EventOverride>();
        while (await reader.ReadAsync())
            result.Add(ReadOverride(reader));

        return result;
    }

    /// <summary>
    /// Deletes every override for the given series. Used by the cascade
    /// in <see cref="EventRepository.DeleteAsync"/> and
    /// <see cref="CalendarRepository.DeleteAsync"/>. Idempotent.
    /// </summary>
    internal static async Task DeleteForSeriesInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid seriesEventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM EventOverrides WHERE SeriesEventId = $seriesEventId;";
        command.Parameters.AddWithValue("$seriesEventId", seriesEventId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Cascade form used by <see cref="CalendarRepository.DeleteAsync"/>:
    /// deletes all overrides whose master event belongs to the given
    /// calendar, inside the caller's transaction. Runs ahead of the
    /// Events delete to satisfy the FK constraint.
    /// </summary>
    internal static async Task DeleteForCalendarInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid calendarId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
        """
        DELETE FROM EventOverrides
        WHERE SeriesEventId IN (
            SELECT Id FROM Events WHERE CalendarId = $calendarId
        );
        """;
        command.Parameters.AddWithValue("$calendarId", calendarId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static EventOverride ReadOverride(SqliteDataReader reader)
    {
        return new EventOverride
        {
            Id = Guid.Parse(reader.GetString(0)),
            SeriesEventId = Guid.Parse(reader.GetString(1)),
            OccurrenceAnchorUtc = ParseUtcDateTime(reader.GetString(2)),
            Title = reader.IsDBNull(3) ? null : reader.GetString(3),
            Description = reader.IsDBNull(4) ? null : reader.GetString(4),
            StartTimeUtc = reader.IsDBNull(5)
                ? null
                : ParseUtcDateTime(reader.GetString(5)),
            EndTimeUtc = reader.IsDBNull(6)
                ? null
                : ParseUtcDateTime(reader.GetString(6)),
            IsAllDay = reader.IsDBNull(7) ? null : reader.GetInt32(7) == 1,
            UpdatedAtUtc = ParseUtcDateTime(reader.GetString(8)),
        };
    }
}
