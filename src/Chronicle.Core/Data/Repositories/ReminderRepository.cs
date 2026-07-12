using Chronicle.Models;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;

namespace Chronicle.Data.Repositories;

/// <summary>
/// Persistence for <see cref="Reminder"/> rows — composed children of the
/// <see cref="Event"/> aggregate (see NOTIFICATIONS.md). Mirrors
/// <see cref="OverrideRepository"/>'s shape: bulk fetch keyed by the owning
/// event, cascade helpers invoked from the event / calendar delete
/// transactions, and no independent lifecycle beyond the aggregate.
///
/// The write shape is <see cref="SetForEventAsync"/> — replace the event's
/// whole reminder set in one transaction. The editor edits the set as a
/// unit (currently 0..1 in the UI, 0..N in the domain), so replace-set is
/// simpler and less stateful than per-row diffing, and it makes the write
/// idempotent for the caller.
/// </summary>
public sealed class ReminderRepository
{
    // OffsetUnit is stored as the enum name text. Hand-written mapping in
    // both directions — no Enum.Parse reflection, AOT-friendly, and an
    // unknown stored value fails loudly instead of round-tripping as junk.
    private static string ToStorage(ReminderOffsetUnit unit) => unit switch
    {
        ReminderOffsetUnit.Minutes => "Minutes",
        ReminderOffsetUnit.Hours => "Hours",
        ReminderOffsetUnit.Days => "Days",
        ReminderOffsetUnit.Weeks => "Weeks",
        _ => throw new InvalidOperationException(
            $"Unknown ReminderOffsetUnit '{unit}'."),
    };

    private static ReminderOffsetUnit FromStorage(string value) => value switch
    {
        "Minutes" => ReminderOffsetUnit.Minutes,
        "Hours" => ReminderOffsetUnit.Hours,
        "Days" => ReminderOffsetUnit.Days,
        "Weeks" => ReminderOffsetUnit.Weeks,
        _ => throw new InvalidOperationException(
            $"Stored OffsetUnit '{value}' does not map to a known "
            + "ReminderOffsetUnit."),
    };

    /// <summary>
    /// Replaces the full reminder set for <paramref name="eventId"/> in a
    /// single transaction: delete existing rows, insert
    /// <paramref name="reminders"/>. Passing an empty list clears the
    /// event's reminders. Each reminder must belong to the event
    /// (<c>EventId</c> mismatch is a caller bug and throws before any
    /// write).
    /// </summary>
#pragma warning disable CA1822 // Mark members as static
    public async Task SetForEventAsync(
        Guid eventId, IReadOnlyList<Reminder> reminders)
#pragma warning restore CA1822 // Mark members as static
    {
        foreach (var reminder in reminders)
        {
            reminder.Validate();
            if (reminder.EventId != eventId)
            {
                throw new InvalidOperationException(
                    "Reminder.EventId does not match the event whose set is "
                    + "being replaced.");
            }
        }

        using var connection = AppDatabase.GetConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var delete = connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText =
                    "DELETE FROM Reminders WHERE EventId = $eventId;";
                delete.Parameters.AddWithValue("$eventId", eventId.ToString());
                await delete.ExecuteNonQueryAsync();
            }

            foreach (var reminder in reminders)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                """
                INSERT INTO Reminders (
                    Id, EventId, OffsetQuantity, OffsetUnit
                )
                VALUES (
                    $id, $eventId, $offsetQuantity, $offsetUnit
                );
                """;
                insert.Parameters.AddWithValue("$id", reminder.Id.ToString());
                insert.Parameters.AddWithValue("$eventId", reminder.EventId.ToString());
                insert.Parameters.AddWithValue("$offsetQuantity", reminder.OffsetQuantity);
                insert.Parameters.AddWithValue("$offsetUnit", ToStorage(reminder.OffsetUnit));
                await insert.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>Returns the reminder set for one event, ordered by offset
    /// (soonest-firing — smallest offset — first).</summary>
    public async Task<List<Reminder>> GetForEventAsync(Guid eventId)
    {
        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

        command.CommandText =
        """
        SELECT Id, EventId, OffsetQuantity, OffsetUnit
        FROM Reminders
        WHERE EventId = $eventId;
        """;
        command.Parameters.AddWithValue("$eventId", eventId.ToString());

        using var reader = await command.ExecuteReaderAsync();

        var result = new List<Reminder>();
        while (await reader.ReadAsync())
            result.Add(ReadReminder(reader));

        result.Sort(CompareByOffset);
        return result;
    }

    /// <summary>
    /// Bulk fetch of reminders for any of the given events, in a single
    /// statement — used by the reconciler's load pass so it never issues
    /// per-event queries. The caller groups by <c>EventId</c>
    /// (<c>EventProjection.GroupRemindersByEvent</c>). Empty input returns
    /// an empty list — avoids constructing an invalid <c>IN ()</c> clause.
    /// </summary>
    public async Task<List<Reminder>> GetForEventsAsync(
        IReadOnlyList<Guid> eventIds)
    {
        if (eventIds.Count == 0)
            return new List<Reminder>();

        using var connection = AppDatabase.GetConnection();
        using var command = connection.CreateCommand();

        var paramNames = new string[eventIds.Count];
        for (int i = 0; i < eventIds.Count; i++)
            paramNames[i] = "$id" + i.ToString(CultureInfo.InvariantCulture);

        command.CommandText =
            "SELECT Id, EventId, OffsetQuantity, OffsetUnit "
            + "FROM Reminders "
            + "WHERE EventId IN (" + string.Join(", ", paramNames) + ");";

        for (int i = 0; i < eventIds.Count; i++)
            command.Parameters.AddWithValue(paramNames[i], eventIds[i].ToString());

        using var reader = await command.ExecuteReaderAsync();

        var result = new List<Reminder>();
        while (await reader.ReadAsync())
            result.Add(ReadReminder(reader));

        result.Sort(CompareByOffset);
        return result;
    }

    /// <summary>
    /// Deletes every reminder for the given event. Cascade helper invoked
    /// from <see cref="EventRepository.DeleteAsync"/>'s transaction, ahead
    /// of the Events delete so the FK is never violated. Idempotent.
    /// </summary>
    internal static async Task DeleteForEventInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "DELETE FROM Reminders WHERE EventId = $eventId;";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Cascade form used by <see cref="CalendarRepository.DeleteAsync"/>:
    /// deletes all reminders whose event belongs to the given calendar,
    /// inside the caller's transaction, ahead of the Events delete.
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
        DELETE FROM Reminders
        WHERE EventId IN (
            SELECT Id FROM Events WHERE CalendarId = $calendarId
        );
        """;
        command.Parameters.AddWithValue("$calendarId", calendarId.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static Reminder ReadReminder(SqliteDataReader reader)
    {
        return new Reminder
        {
            Id = Guid.Parse(reader.GetString(0)),
            EventId = Guid.Parse(reader.GetString(1)),
            OffsetQuantity = reader.GetInt32(2),
            OffsetUnit = FromStorage(reader.GetString(3)),
        };
    }

    // Soonest-firing first (smallest offset = closest to the event start),
    // ties broken by Id for determinism. SQL ORDER BY can't sort by the
    // derived minute value without duplicating the unit table in SQL, so
    // ordering lives here.
    private static int CompareByOffset(Reminder a, Reminder b)
    {
        var byMinutes = a.OffsetMinutes.CompareTo(b.OffsetMinutes);
        if (byMinutes != 0) return byMinutes;
        return a.Id.CompareTo(b.Id);
    }
}
