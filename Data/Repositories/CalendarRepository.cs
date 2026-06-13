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
    /// Deletes a calendar and all of its events in a single transaction.
    /// Events are cascade-deleted here (rather than relying on a schema
    /// ON DELETE CASCADE) so existing databases work without migration and
    /// the foreign-key constraint is never violated. See DECISIONS.md.
    /// </summary>
    public async Task DeleteAsync(Guid id)
    {
        using var connection =
            AppDatabase.GetConnection();

        using var transaction = connection.BeginTransaction();
        try
        {
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
}