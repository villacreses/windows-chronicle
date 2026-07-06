using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Chronicle.Data.Repositories;
using Chronicle.Models;
using Chronicle.Models.Recurrence;
using static Chronicle.Tests.Data.RepositoryTestData;

namespace Chronicle.Tests.Data;

/// <summary>
/// Candidate-shape contract for
/// <see cref="EventRepository.SearchCandidatesAsync"/>. The method returns
/// candidate ROWS the projection layer will expand and re-filter —
/// standalone rows matching Title / Description, plus recurring masters
/// where either the master itself or one of its <c>EventOverrides</c>
/// matches. The window filter matches <see cref="EventRepository.GetInRangeAsync"/>;
/// series pruned there stay pruned here. Occurrence-level filtering
/// belongs to <c>EventProjection.SearchOccurrences</c> and is exercised
/// there, not here.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EventRepositorySearchTests : InitializedDatabaseTest
{
    private readonly CalendarRepository _calendars = new();
    private readonly EventRepository _events = new();
    private readonly OverrideRepository _overrides = new();

    private static readonly DateTime RangeStart = Utc(2026, 6, 10, 0, 0);
    private static readonly DateTime RangeEnd = Utc(2026, 6, 20, 0, 0);

    private async Task<Guid> SeedCalendarAsync()
    {
        var calendar = NewCalendar();
        await _calendars.InsertAsync(calendar);
        return calendar.Id;
    }

    private async Task<HashSet<Guid>> IdsAsync(string query)
    {
        var rows = await _events.SearchCandidatesAsync(query, RangeStart, RangeEnd);
        return rows.Select(e => e.Id).ToHashSet();
    }

    [Fact]
    public async Task EmptyQuery_ReturnsEmpty_NeverMatchesEverything()
    {
        var calendarId = await SeedCalendarAsync();
        await _events.InsertAsync(StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 12), title: "Anything"));

        Assert.Empty(await _events.SearchCandidatesAsync("", RangeStart, RangeEnd));
        Assert.Empty(await _events.SearchCandidatesAsync("   ", RangeStart, RangeEnd));
    }

    [Fact]
    public async Task MatchesTitleAndDescription_CaseInsensitive_ExcludesNonMatches()
    {
        var calendarId = await SeedCalendarAsync();

        var titleHit = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 12), title: "Standup meeting");
        var descriptionHit = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 13),
            title: "Sync", description: "Weekly team STANDUP notes");
        var miss = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 14), title: "Lunch");

        foreach (var e in new[] { titleHit, descriptionHit, miss })
            await _events.InsertAsync(e);

        var ids = await IdsAsync("standup");

        Assert.Contains(titleHit.Id, ids);
        Assert.Contains(descriptionHit.Id, ids);
        Assert.DoesNotContain(miss.Id, ids);
    }

    [Fact]
    public async Task LikeMetacharacters_AreEscaped_MatchAsLiterals()
    {
        // A user typing "50%" should get "Q3 goals 50%" but NOT "Q3 goals 50 done".
        // Same for underscore.
        var calendarId = await SeedCalendarAsync();

        var literalPercent = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 12), title: "Q3 goals 50%");
        var wildcardWouldMatch = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 13), title: "Q3 goals 50 done");
        var literalUnderscore = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 14), title: "code_review");
        var underscoreWildcardWouldMatch = StandaloneEvent(
            calendarId, startUtc: Utc(2026, 6, 15), title: "code X review");

        foreach (var e in new[]
        {
            literalPercent, wildcardWouldMatch, literalUnderscore, underscoreWildcardWouldMatch
        })
            await _events.InsertAsync(e);

        var percentIds = await IdsAsync("50%");
        Assert.Contains(literalPercent.Id, percentIds);
        Assert.DoesNotContain(wildcardWouldMatch.Id, percentIds);

        var underscoreIds = await IdsAsync("code_review");
        Assert.Contains(literalUnderscore.Id, underscoreIds);
        Assert.DoesNotContain(underscoreWildcardWouldMatch.Id, underscoreIds);
    }

    [Fact]
    public async Task RecurringMaster_MatchesViaOverride_EvenIfMasterFieldsDoNotMatch()
    {
        // The "override-only match" case: the master's Title and Description
        // don't contain the query, but an EventOverride's Title does. The
        // union at the SQL layer must surface the master so the projection
        // pipeline can expand it and find the matching occurrence.
        var calendarId = await SeedCalendarAsync();

        var master = RecurringMaster(
            calendarId, rrule: "FREQ=DAILY", startUtc: Utc(2026, 6, 11, 9, 0),
            title: "Series");
        await _events.InsertAsync(master);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 13, 9, 0)),
            new OverrideFields(Title: "Renamed lunch"));

        var ids = await IdsAsync("lunch");

        Assert.Contains(master.Id, ids);
    }

    [Fact]
    public async Task RecurringMaster_MatchesViaOverrideDescription_EvenIfMasterFieldsDoNotMatch()
    {
        var calendarId = await SeedCalendarAsync();

        var master = RecurringMaster(
            calendarId, rrule: "FREQ=DAILY", startUtc: Utc(2026, 6, 11, 9, 0),
            title: "Series");
        await _events.InsertAsync(master);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(master.Id, Utc(2026, 6, 13, 9, 0)),
            new OverrideFields(Description: "Reservation at Tosca"));

        var ids = await IdsAsync("tosca");

        Assert.Contains(master.Id, ids);
    }

    [Fact]
    public async Task RecurringMaster_MatchingButPrunedByWindow_IsExcluded()
    {
        // The candidate step still respects the same window pruning as
        // GetInRangeAsync — a title match on a dead series must not leak
        // into the results.
        var calendarId = await SeedCalendarAsync();

        var deadTitleHit = RecurringMaster(
            calendarId, rrule: "FREQ=WEEKLY;COUNT=2",
            startUtc: Utc(2026, 6, 1, 9, 0),
            endUtcCached: Utc(2026, 6, 5, 9, 0),
            title: "Dead standup");
        await _events.InsertAsync(deadTitleHit);

        var ids = await IdsAsync("standup");

        Assert.DoesNotContain(deadTitleHit.Id, ids);
    }

    [Fact]
    public async Task NonRecurringMatch_WithOnlyOverrideOnDifferentSeries_DoesNotUnionOthers()
    {
        // Sanity: matching an override on series A must not accidentally
        // pull in an unrelated series B.
        var calendarId = await SeedCalendarAsync();

        var seriesA = RecurringMaster(
            calendarId, rrule: "FREQ=DAILY", startUtc: Utc(2026, 6, 11, 9, 0),
            title: "Alpha");
        var seriesB = RecurringMaster(
            calendarId, rrule: "FREQ=DAILY", startUtc: Utc(2026, 6, 11, 10, 0),
            title: "Beta");
        await _events.InsertAsync(seriesA);
        await _events.InsertAsync(seriesB);

        await _overrides.UpsertAsync(
            new EventRef.Occurrence(seriesA.Id, Utc(2026, 6, 13, 9, 0)),
            new OverrideFields(Title: "unique-token"));

        var ids = await IdsAsync("unique-token");

        Assert.Contains(seriesA.Id, ids);
        Assert.DoesNotContain(seriesB.Id, ids);
    }
}
