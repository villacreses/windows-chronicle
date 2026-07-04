using System;
using System.Linq;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models.Recurrence;
using Microsoft.Data.Sqlite;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// <see cref="OverrideRepository"/> upsert and fetch against isolated SQLite.
/// The load-bearing contract is one override row per
/// <c>(SeriesEventId, OccurrenceAnchorUtc)</c> — enforced by the unique key
/// via ON CONFLICT DO UPDATE — and a bulk fetch that returns every override
/// for the requested series (and nothing for empty input, which must not
/// build an invalid <c>IN ()</c> clause). Overrides FK to a master event, so
/// each test seeds the master first.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OverrideRepositoryTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly OverrideRepository _overrides = new();

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    private async Task<Guid> SeedMasterAsync(Guid calendarId)
    {
        var master = RecurringMaster(calendarId, startUtc: Utc(2026, 6, 1, 9, 0));
        await _events.InsertAsync(master);
        return master.Id;
    }

    [Fact]
    public async Task Upsert_InsertsOverride_FieldsRoundTrip()
    {
        var calendarId = await SeedCalendarAsync();
        var masterId = await SeedMasterAsync(calendarId);
        var anchor = Utc(2026, 6, 1, 9, 0);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, anchor),
            new OverrideFields(
                Title: "Moved",
                Description: "Rescheduled",
                StartTimeUtc: Utc(2026, 6, 1, 10, 0),
                EndTimeUtc: Utc(2026, 6, 1, 11, 0),
                IsAllDay: false));

        var ovr = Assert.Single(await _overrides.GetForSeriesAsync(masterId));
        Assert.Equal(masterId, ovr.SeriesEventId);
        Assert.Equal(anchor, ovr.OccurrenceAnchorUtc);
        Assert.Equal("Moved", ovr.Title);
        Assert.Equal("Rescheduled", ovr.Description);
        Assert.Equal(Utc(2026, 6, 1, 10, 0), ovr.StartTimeUtc);
        Assert.Equal(Utc(2026, 6, 1, 11, 0), ovr.EndTimeUtc);
        Assert.False(ovr.IsAllDay);
        Assert.Equal(DateTimeKind.Utc, ovr.OccurrenceAnchorUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, ovr.StartTimeUtc!.Value.Kind);
    }

    [Fact]
    public async Task Upsert_NullFields_PersistAsNullForInheritance()
    {
        var calendarId = await SeedCalendarAsync();
        var masterId = await SeedMasterAsync(calendarId);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "Only the title changed"));

        var ovr = Assert.Single(await _overrides.GetForSeriesAsync(masterId));
        Assert.Equal("Only the title changed", ovr.Title);
        Assert.Null(ovr.Description);
        Assert.Null(ovr.StartTimeUtc);
        Assert.Null(ovr.EndTimeUtc);
        Assert.Null(ovr.IsAllDay);
    }

    [Fact]
    public async Task Upsert_SameAnchorTwice_UpdatesInPlace_PreservingRowId()
    {
        var calendarId = await SeedCalendarAsync();
        var masterId = await SeedMasterAsync(calendarId);
        var anchor = Utc(2026, 6, 1, 9, 0);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, anchor),
            new OverrideFields(Title: "First"));
        var firstId = (await _overrides.GetForSeriesAsync(masterId)).Single().Id;

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, anchor),
            new OverrideFields(Title: "Second", StartTimeUtc: Utc(2026, 6, 1, 10, 0)));

        // One row for the (series, anchor) key — updated in place, not appended.
        var ovr = Assert.Single(await _overrides.GetForSeriesAsync(masterId));
        Assert.Equal(firstId, ovr.Id);
        Assert.Equal("Second", ovr.Title);
        Assert.Equal(Utc(2026, 6, 1, 10, 0), ovr.StartTimeUtc);
    }

    [Fact]
    public async Task Upsert_DifferentAnchors_CreateSeparateRowsOrderedByAnchor()
    {
        var calendarId = await SeedCalendarAsync();
        var masterId = await SeedMasterAsync(calendarId);

        // Insert out of order to prove the query orders by anchor.
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, Utc(2026, 6, 8, 9, 0)),
            new OverrideFields(Title: "Second week"));
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(masterId, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "First week"));

        var list = await _overrides.GetForSeriesAsync(masterId);
        Assert.Equal(2, list.Count);
        Assert.Equal(Utc(2026, 6, 1, 9, 0), list[0].OccurrenceAnchorUtc);
        Assert.Equal(Utc(2026, 6, 8, 9, 0), list[1].OccurrenceAnchorUtc);
    }

    [Fact]
    public async Task BulkFetch_ReturnsAllOverridesForRequestedSeries_NoneForOthers()
    {
        var calendarId = await SeedCalendarAsync();
        var seriesA = await SeedMasterAsync(calendarId);
        var seriesB = await SeedMasterAsync(calendarId);
        var seriesC = await SeedMasterAsync(calendarId);

        // Series A has two overrides — "all for the series" must return both.
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(seriesA, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "A1"));
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(seriesA, Utc(2026, 6, 8, 9, 0)),
            new OverrideFields(Title: "A2"));
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(seriesB, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "B1"));
        // Series C's override must NOT come back.
        await _overrides.UpsertAsync(
            new EventRef.Occurrence(seriesC, Utc(2026, 6, 1, 9, 0)),
            new OverrideFields(Title: "C1"));

        var result = await _overrides.GetForSeriesAsync(new[] { seriesA, seriesB });

        Assert.Equal(3, result.Count);
        var seriesIds = result.Select(o => o.SeriesEventId).ToHashSet();
        Assert.Contains(seriesA, seriesIds);
        Assert.Contains(seriesB, seriesIds);
        Assert.DoesNotContain(seriesC, seriesIds);
    }

    [Fact]
    public async Task BulkFetch_EmptyInput_ReturnsEmpty()
    {
        // Empty input must short-circuit rather than emit an invalid IN ().
        var result = await _overrides.GetForSeriesAsync(Array.Empty<Guid>());
        Assert.Empty(result);
    }

    [Fact]
    public async Task Upsert_NonUtcAnchor_ThrowsAtRepositoryBoundary()
    {
        // ValidateFields guards the write boundary before any SQL runs, so no
        // seeded master is needed.
        var unspecifiedAnchor = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Unspecified);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _overrides.UpsertAsync(
                new EventRef.Occurrence(Guid.NewGuid(), unspecifiedAnchor),
                new OverrideFields(Title: "x")));
        Assert.Contains("UTC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_NonUtcStart_ThrowsAtRepositoryBoundary()
    {
        var nonUtcStart = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Unspecified);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _overrides.UpsertAsync(
                new EventRef.Occurrence(Guid.NewGuid(), Utc(2026, 6, 1, 9, 0)),
                new OverrideFields(StartTimeUtc: nonUtcStart)));
        Assert.Contains("UTC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_NonUtcEnd_ThrowsAtRepositoryBoundary()
    {
        var nonUtcEnd = new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Unspecified);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _overrides.UpsertAsync(
                new EventRef.Occurrence(Guid.NewGuid(), Utc(2026, 6, 1, 9, 0)),
                new OverrideFields(EndTimeUtc: nonUtcEnd)));
        Assert.Contains("UTC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_EndBeforeStart_ThrowsAtRepositoryBoundary()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _overrides.UpsertAsync(
                new EventRef.Occurrence(Guid.NewGuid(), Utc(2026, 6, 1, 9, 0)),
                new OverrideFields(
                    StartTimeUtc: Utc(2026, 6, 1, 11, 0),
                    EndTimeUtc: Utc(2026, 6, 1, 10, 0))));
        Assert.Contains("before", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upsert_OrphanSeries_ViolatesForeignKey()
    {
        // Fields are valid, so the write reaches SQLite; with foreign_keys = ON,
        // EventOverrides.SeriesEventId → Events(Id) rejects an override whose
        // master row does not exist.
        await Assert.ThrowsAsync<SqliteException>(
            () => _overrides.UpsertAsync(
                new EventRef.Occurrence(Guid.NewGuid(), Utc(2026, 6, 1, 9, 0)),
                new OverrideFields(Title: "orphan")));
    }
}
