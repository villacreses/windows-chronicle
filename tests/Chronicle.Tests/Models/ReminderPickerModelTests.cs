using System;
using System.Collections.Generic;
using Chronicle.Models;

namespace Chronicle.Tests.Models;

/// <summary>
/// Layer 5 — the "Remind me" picker logic extracted from
/// <c>EventEditPopover</c> into <see cref="ReminderPickerModel"/>. Covers
/// representability, seeding, and — the Local Baseline Addendum's
/// correctness fix — that an existing reminder set the 0..1-preset editor
/// cannot represent is preserved verbatim on save rather than silently
/// destroyed. See DECISIONS.md "Reminders: Post-Ship Audit Positions".
/// </summary>
public sealed class ReminderPickerModelTests
{
    private static Reminder ReminderFor(
        Guid eventId, int quantity, ReminderOffsetUnit unit, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        EventId = eventId,
        OffsetQuantity = quantity,
        OffsetUnit = unit,
    };

    // ── Representability ──────────────────────────────────────────────────

    [Fact]
    public void IsRepresentable_Empty_True()
    {
        Assert.True(ReminderPickerModel.IsRepresentable(Array.Empty<Reminder>()));
    }

    [Fact]
    public void IsRepresentable_SinglePresetOffset_True()
    {
        var eventId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 10, ReminderOffsetUnit.Minutes) };

        Assert.True(ReminderPickerModel.IsRepresentable(existing));
    }

    [Fact]
    public void IsRepresentable_SingleNonPresetOffset_False()
    {
        // 45 minutes matches no preset (10, 30, or a coarser unit).
        var eventId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 45, ReminderOffsetUnit.Minutes) };

        Assert.False(ReminderPickerModel.IsRepresentable(existing));
    }

    [Fact]
    public void IsRepresentable_MultipleReminders_False()
    {
        // Two reminders is unrepresentable even though each individually
        // matches a preset — the editor's slot is 0..1, not 0..N.
        var eventId = Guid.NewGuid();
        var existing = new[]
        {
            ReminderFor(eventId, 10, ReminderOffsetUnit.Minutes),
            ReminderFor(eventId, 1, ReminderOffsetUnit.Days),
        };

        Assert.False(ReminderPickerModel.IsRepresentable(existing));
    }

    // ── Labels / seeding ───────────────────────────────────────────────────

    [Fact]
    public void BuildLabels_Representable_OmitsSentinel()
    {
        var labels = ReminderPickerModel.BuildLabels(Array.Empty<Reminder>());

        Assert.DoesNotContain(ReminderPickerModel.KeptAsIsLabel, labels);
        Assert.Equal(ReminderPickerModel.Presets.Count, labels.Count);
    }

    [Fact]
    public void BuildLabels_Unrepresentable_PrependsSentinel()
    {
        var eventId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 45, ReminderOffsetUnit.Minutes) };

        var labels = ReminderPickerModel.BuildLabels(existing);

        Assert.Equal(ReminderPickerModel.KeptAsIsLabel, labels[0]);
        Assert.Equal(ReminderPickerModel.Presets.Count + 1, labels.Count);
    }

    [Fact]
    public void SeedIndex_Empty_SeedsNoReminder()
    {
        Assert.Equal(0, ReminderPickerModel.SeedIndex(Array.Empty<Reminder>()));
    }

    [Fact]
    public void SeedIndex_MatchingPreset_SeedsThatPreset()
    {
        var eventId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 1, ReminderOffsetUnit.Hours) };

        var index = ReminderPickerModel.SeedIndex(existing);

        var seeded = ReminderPickerModel.Presets[index];
        Assert.Equal(1, seeded.Quantity);
        Assert.Equal(ReminderOffsetUnit.Hours, seeded.Unit);
    }

    [Fact]
    public void SeedIndex_Unrepresentable_SeedsSentinel()
    {
        var eventId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 45, ReminderOffsetUnit.Minutes) };

        Assert.Equal(0, ReminderPickerModel.SeedIndex(existing));
        Assert.Equal(ReminderPickerModel.KeptAsIsLabel,
            ReminderPickerModel.BuildLabels(existing)[ReminderPickerModel.SeedIndex(existing)]);
    }

    // ── ResolveForSave: the preservation invariant ────────────────────────

    [Fact]
    public void ResolveForSave_UnrepresentableSet_SentinelUntouched_PreservesVerbatim()
    {
        // The audit's core finding: saving without touching the picker must
        // never destroy a reminder set the editor cannot display.
        var eventId = Guid.NewGuid();
        var keep1Id = Guid.NewGuid();
        var keep2Id = Guid.NewGuid();
        var existing = new[]
        {
            ReminderFor(eventId, 45, ReminderOffsetUnit.Minutes, keep1Id),
            ReminderFor(eventId, 3, ReminderOffsetUnit.Days, keep2Id),
        };

        var result = ReminderPickerModel.ResolveForSave(existing, selectedIndex: 0, eventId);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Id == keep1Id && r.OffsetQuantity == 45);
        Assert.Contains(result, r => r.Id == keep2Id && r.OffsetQuantity == 3);
    }

    [Fact]
    public void ResolveForSave_UnrepresentableSet_ExplicitPresetChosen_ReplacesWholeSet()
    {
        // Choosing a preset is the only control the editor offers; using it
        // is an explicit, intentional narrowing, not silent destruction.
        var eventId = Guid.NewGuid();
        var existing = new[]
        {
            ReminderFor(eventId, 45, ReminderOffsetUnit.Minutes),
            ReminderFor(eventId, 3, ReminderOffsetUnit.Days),
        };
        // Sentinel is index 0, so "10 minutes before" (Presets[2]) is index 3.
        var tenMinutesIndex = 3;
        Assert.Equal(10, ReminderPickerModel.Presets[2].Quantity);

        var result = ReminderPickerModel.ResolveForSave(existing, tenMinutesIndex, eventId);

        var only = Assert.Single(result);
        Assert.Equal(10, only.OffsetQuantity);
        Assert.Equal(ReminderOffsetUnit.Minutes, only.OffsetUnit);
        Assert.DoesNotContain(only.Id, new[] { existing[0].Id, existing[1].Id });
    }

    [Fact]
    public void ResolveForSave_RepresentableEmpty_NoReminderSelected_StaysEmpty()
    {
        var eventId = Guid.NewGuid();

        var result = ReminderPickerModel.ResolveForSave(
            Array.Empty<Reminder>(), selectedIndex: 0, eventId);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveForSave_RepresentableSingle_OffsetUnchanged_PreservesId()
    {
        var eventId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 10, ReminderOffsetUnit.Minutes, reminderId) };
        var sameIndex = ReminderPickerModel.SeedIndex(existing);

        var result = ReminderPickerModel.ResolveForSave(existing, sameIndex, eventId);

        var only = Assert.Single(result);
        Assert.Equal(reminderId, only.Id);
    }

    [Fact]
    public void ResolveForSave_RepresentableSingle_OffsetChanged_MintsNewId()
    {
        var eventId = Guid.NewGuid();
        var reminderId = Guid.NewGuid();
        var existing = new[] { ReminderFor(eventId, 10, ReminderOffsetUnit.Minutes, reminderId) };
        var thirtyMinutesIndex = 3; // Presets[3] = "30 minutes before"
        Assert.Equal(30, ReminderPickerModel.Presets[3].Quantity);

        var result = ReminderPickerModel.ResolveForSave(existing, thirtyMinutesIndex, eventId);

        var only = Assert.Single(result);
        Assert.NotEqual(reminderId, only.Id);
        Assert.Equal(30, only.OffsetQuantity);
    }

    [Fact]
    public void ResolveForSave_AllResults_CarryTheGivenEventId()
    {
        var eventId = Guid.NewGuid();

        var noReminder = ReminderPickerModel.ResolveForSave(
            Array.Empty<Reminder>(), selectedIndex: 1, eventId); // "At start time"

        var only = Assert.Single(noReminder);
        Assert.Equal(eventId, only.EventId);
    }
}
