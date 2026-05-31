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