using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// <see cref="ReminderRepository"/> against isolated SQLite. The
/// load-bearing contracts: <c>SetForEventAsync</c> replaces the event's
/// whole reminder set transactionally (empty list clears it); the offset
/// round-trips as the user expressed it — <c>(2, Weeks)</c> comes back as
/// <c>(2, Weeks)</c>, never a normalized minute count; bulk fetch returns
/// every reminder for the requested events (and nothing for empty input);
/// and reminders cascade with their event and with their calendar, in the
/// same transactions that already cascade <c>EventOverrides</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReminderRepositoryTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly ReminderRepository _reminders = new();

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    private async Task<Event> SeedEventAsync(Guid calendarId)
    {
        var evt = StandaloneEvent(calendarId, startUtc: Utc(2026, 7, 20, 9, 0));
        await _events.InsertAsync(evt);
        return evt;
    }

    [Fact]
    public async Task SetForEvent_InsertsReminders_OffsetRoundTripsAsExpressed()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = await SeedEventAsync(calendarId);

        // "2 weeks before" must come back as (2, Weeks) — not 20160 minutes,
        // and not (14, Days). Preserving the user's representation is the
        // point of the (Quantity, Unit) shape.
        await _reminders.SetForEventAsync(evt.Id, new[]
        {
            NewReminder(evt.Id, quantity: 2, unit: ReminderOffsetUnit.Weeks),
        });

        var loaded = Assert.Single(await _reminders.GetForEventAsync(evt.Id));
        Assert.Equal(evt.Id, loaded.EventId);
        Assert.Equal(2, loaded.OffsetQuantity);
        Assert.Equal(ReminderOffsetUnit.Weeks, loaded.OffsetUnit);
        Assert.Equal(20160, loaded.OffsetMinutes); // derived, not stored
    }

    [Fact]
    public async Task SetForEvent_ReplacesExistingSet()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = await SeedEventAsync(calendarId);

        await _reminders.SetForEventAsync(evt.Id, new[]
        {
            NewReminder(evt.Id, 10, ReminderOffsetUnit.Minutes),
            NewReminder(evt.Id, 1, ReminderOffsetUnit.Days),
        });

        // Replace with a different single reminder — the old two must be gone.
        var replacement = NewReminder(evt.Id, 1, ReminderOffsetUnit.Hours);
        await _reminders.SetForEventAsync(evt.Id, new[] { replacement });

        var loaded = Assert.Single(await _reminders.GetForEventAsync(evt.Id));
        Assert.Equal(replacement.Id, loaded.Id);
        Assert.Equal(1, loaded.OffsetQuantity);
        Assert.Equal(ReminderOffsetUnit.Hours, loaded.OffsetUnit);
    }

    [Fact]
    public async Task SetForEvent_EmptyList_ClearsReminders()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = await SeedEventAsync(calendarId);
        await _reminders.SetForEventAsync(evt.Id, new[] { NewReminder(evt.Id) });

        await _reminders.SetForEventAsync(evt.Id, Array.Empty<Reminder>());

        Assert.Empty(await _reminders.GetForEventAsync(evt.Id));
    }

    [Fact]
    public async Task SetForEvent_MismatchedEventId_Throws_WritesNothing()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = await SeedEventAsync(calendarId);
        var other = await SeedEventAsync(calendarId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _reminders.SetForEventAsync(evt.Id, new[] { NewReminder(other.Id) }));

        Assert.Empty(await _reminders.GetForEventAsync(evt.Id));
        Assert.Empty(await _reminders.GetForEventAsync(other.Id));
    }

    [Fact]
    public async Task GetForEvents_BulkFetch_ReturnsAllRequested_OrderedSoonestFirst()
    {
        var calendarId = await SeedCalendarAsync();
        var a = await SeedEventAsync(calendarId);
        var b = await SeedEventAsync(calendarId);
        var unrelated = await SeedEventAsync(calendarId);

        await _reminders.SetForEventAsync(a.Id, new[]
        {
            NewReminder(a.Id, 1, ReminderOffsetUnit.Days),
            NewReminder(a.Id, 10, ReminderOffsetUnit.Minutes),
        });
        await _reminders.SetForEventAsync(b.Id, new[]
        {
            NewReminder(b.Id, 1, ReminderOffsetUnit.Hours),
        });
        await _reminders.SetForEventAsync(unrelated.Id, new[]
        {
            NewReminder(unrelated.Id, 5, ReminderOffsetUnit.Minutes),
        });

        var loaded = await _reminders.GetForEventsAsync(new[] { a.Id, b.Id });

        Assert.Equal(3, loaded.Count);
        Assert.DoesNotContain(loaded, r => r.EventId == unrelated.Id);
        // Soonest-firing (smallest offset) first.
        Assert.Equal(
            new[] { 10, 60, 1440 },
            loaded.Select(r => r.OffsetMinutes));
    }

    [Fact]
    public async Task GetForEvents_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(await _reminders.GetForEventsAsync(Array.Empty<Guid>()));
    }

    [Fact]
    public async Task EventDelete_CascadesReminders()
    {
        var calendarId = await SeedCalendarAsync();
        var evt = await SeedEventAsync(calendarId);
        var surviving = await SeedEventAsync(calendarId);
        await _reminders.SetForEventAsync(evt.Id, new[] { NewReminder(evt.Id) });
        await _reminders.SetForEventAsync(surviving.Id, new[] { NewReminder(surviving.Id) });

        await _events.DeleteAsync(evt.Id);

        Assert.Empty(await _reminders.GetForEventAsync(evt.Id));
        Assert.Single(await _reminders.GetForEventAsync(surviving.Id));
    }

    [Fact]
    public async Task CalendarDelete_CascadesReminders_OfItsEventsOnly()
    {
        var doomedCalendarId = await SeedCalendarAsync();
        var survivingCalendarId = await SeedCalendarAsync();
        var doomedEvt = await SeedEventAsync(doomedCalendarId);
        var survivingEvt = await SeedEventAsync(survivingCalendarId);
        await _reminders.SetForEventAsync(doomedEvt.Id, new[] { NewReminder(doomedEvt.Id) });
        await _reminders.SetForEventAsync(survivingEvt.Id, new[] { NewReminder(survivingEvt.Id) });

        await _calendars.DeleteAsync(doomedCalendarId);

        Assert.Empty(await _reminders.GetForEventAsync(doomedEvt.Id));
        Assert.Single(await _reminders.GetForEventAsync(survivingEvt.Id));
    }
}
