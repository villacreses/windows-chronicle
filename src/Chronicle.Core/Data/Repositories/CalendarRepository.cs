using Chronicle.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chronicle.Data.Repositories;

public sealed class CalendarRepository
{
#pragma warning disable CA1822 // Mark members as static
    public async Task InsertAsync(Calendar calendar)
#pragma warning restore CA1822 // Mark members as static
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        INSERT INTO Calendars (
            Id,
            Name,
            Color
        )
        VALUES (
            $id,
            $name,
            $color
        );
        """;

        command.Parameters.AddWithValue(
            "$id",
            calendar.Id.ToString());

        command.Parameters.AddWithValue(
            "$name",
            calendar.Name);

        command.Parameters.AddWithValue(
            "$color",
            calendar.Color);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateAsync(Calendar calendar)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        UPDATE Calendars SET
            Name  = $name,
            Color = $color
        WHERE Id = $id;
        """;

        command.Parameters.AddWithValue("$id", calendar.Id.ToString());
        command.Parameters.AddWithValue("$name", calendar.Name);
        command.Parameters.AddWithValue("$color", calendar.Color);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Deletes a calendar, all of its events, and all of its events'
    /// per-occurrence overrides in a single transaction. The cascade
    /// runs from deepest dependent table outward so each FK constraint
    /// is satisfied in turn (overrides → events → calendar). Lives in
    /// the repository rather than a schema ON DELETE CASCADE — see
    /// DECISIONS.md.
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var transaction = connection.BeginTransaction();
        try
        {
            await OverrideRepository.DeleteForCalendarInTransactionAsync(
                connection, transaction, id);

            using (var deleteEvents = connection.CreateCommand())
            {
                deleteEvents.Transaction = transaction;
                deleteEvents.CommandText =
                    "DELETE FROM Events WHERE CalendarId = $id;";
                deleteEvents.Parameters.AddWithValue("$id", id.ToString());
                await deleteEvents.ExecuteNonQueryAsync();
            }

            using (var deleteCalendar = connection.CreateCommand())
            {
                deleteCalendar.Transaction = transaction;
                deleteCalendar.CommandText =
                    "DELETE FROM Calendars WHERE Id = $id;";
                deleteCalendar.Parameters.AddWithValue("$id", id.ToString());
                await deleteCalendar.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<List<Calendar>> GetAllAsync()
    {
        using var connection =
            AppDatabase.GetConnection();

        using var command =
            connection.CreateCommand();

        command.CommandText =
        """
        SELECT
            Id,
            Name,
            Color
        FROM Calendars
        ORDER BY Name;
        """;

        using var reader =
            await command.ExecuteReaderAsync();

        var calendars = new List<Calendar>();

        while (await reader.ReadAsync())
        {
            calendars.Add(
                new Calendar
                {
                    Id = Guid.Parse(
                        reader.GetString(0)),

                    Name = reader.GetString(1),

                    Color = reader.GetString(2)
                });
        }

        return calendars;
    }

    /// <summary>
    /// Enforces the app-level invariant that at least one calendar always
    /// exists: if the Calendars table is empty, inserts a single "Default"
    /// calendar (teal accent). Idempotent — a no-op the moment any calendar
    /// exists — so it is safe to call on every startup and after any calendar
    /// delete. Deliberately not folded into <see cref="AppDatabase.Initialize"/>
    /// so the test host's isolated databases stay empty unless a test opts in.
    /// </summary>
    public async Task EnsureDefaultAsync()
    {
        using (var connection = AppDatabase.GetConnection())
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText =
                "SELECT COUNT(*) FROM Calendars;";

            var count = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync());

            if (count > 0)
                return;
        }

        await InsertAsync(
            new Calendar
            {
                Id = Guid.NewGuid(),
                Name = "Default",
                Color = Calendar.DefaultColorHex,
            });
    }
}