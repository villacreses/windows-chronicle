using System;
using System.Collections.Generic;
using System.Linq;

namespace Chronicle.Models;

/// <summary>One preset the "Remind me" picker offers. <see cref="Quantity"/>
/// null is the "No reminder" option.</summary>
internal readonly record struct ReminderPreset(string Label, int? Quantity, ReminderOffsetUnit Unit);

/// <summary>
/// The pure "Remind me" picker logic extracted from <c>EventEditPopover</c>
/// (see <c>.context/TESTING.md</c> Layer 5): the fixed preset list, seeding
/// the picker from an event's existing reminder set, and resolving the
/// picker's selection back into the reminder set to persist.
///
/// The editor exposes 0..1 reminders from a fixed preset list; the domain
/// and the write path support 0..N reminders at any expressed offset (see
/// REMINDERS.md "Editor scope vs. domain capability"). When an event's
/// existing reminder set does not fit that 0..1-preset shape — more than
/// one reminder, or a single reminder whose offset matches no preset — this
/// model surfaces a synthetic "kept as-is" entry rather than silently
/// falling back to "No reminder": saving with that entry still selected
/// preserves the existing set verbatim (same reminders, same Ids). Selecting
/// any other entry is an explicit user choice and replaces the whole set
/// with the single chosen preset, exactly as the 0..1 editor model promises.
/// See DECISIONS.md "Reminders: Post-Ship Audit Positions" — a poorer UI
/// over a richer model must never destroy state it cannot display.
/// </summary>
internal static class ReminderPickerModel
{
    public const string KeptAsIsLabel = "Custom (kept as-is)";

    public static readonly IReadOnlyList<ReminderPreset> Presets = new[]
    {
        new ReminderPreset("No reminder",       null, ReminderOffsetUnit.Minutes),
        new ReminderPreset("At start time",     0,    ReminderOffsetUnit.Minutes),
        new ReminderPreset("10 minutes before", 10,   ReminderOffsetUnit.Minutes),
        new ReminderPreset("30 minutes before", 30,   ReminderOffsetUnit.Minutes),
        new ReminderPreset("1 hour before",     1,    ReminderOffsetUnit.Hours),
        new ReminderPreset("1 day before",      1,    ReminderOffsetUnit.Days),
        new ReminderPreset("1 week before",     1,    ReminderOffsetUnit.Weeks),
        new ReminderPreset("2 weeks before",    2,    ReminderOffsetUnit.Weeks),
    };

    /// <summary>
    /// True when <paramref name="existing"/> fits the editor's 0..1-preset
    /// shape: empty, or exactly one reminder whose offset matches a preset
    /// exactly.
    /// </summary>
    public static bool IsRepresentable(IReadOnlyList<Reminder> existing)
    {
        if (existing.Count == 0)
            return true;
        if (existing.Count > 1)
            return false;

        var r = existing[0];
        return Presets.Any(p => p.Quantity == r.OffsetQuantity && p.Unit == r.OffsetUnit);
    }

    /// <summary>
    /// The labels the picker's ComboBox should show, in display order. When
    /// <paramref name="existing"/> is not representable, <see cref="KeptAsIsLabel"/>
    /// is prepended so the unrepresentable state has an explicit, truthful
    /// entry instead of a silent fallback to "No reminder".
    /// </summary>
    public static IReadOnlyList<string> BuildLabels(IReadOnlyList<Reminder> existing)
    {
        var presetLabels = Presets.Select(p => p.Label);
        return IsRepresentable(existing)
            ? presetLabels.ToArray()
            : new[] { KeptAsIsLabel }.Concat(presetLabels).ToArray();
    }

    /// <summary>
    /// The index into <see cref="BuildLabels"/>'s output to preselect. An
    /// unrepresentable set seeds the sentinel (index 0); a representable set
    /// seeds the matching preset (index 0 = "No reminder" when empty).
    /// </summary>
    public static int SeedIndex(IReadOnlyList<Reminder> existing)
    {
        if (!IsRepresentable(existing))
            return 0;
        if (existing.Count == 0)
            return 0;

        var r = existing[0];
        var index = Presets.ToList().FindIndex(
            p => p.Quantity == r.OffsetQuantity && p.Unit == r.OffsetUnit);
        return index >= 0 ? index : 0; // unreachable when IsRepresentable is true
    }

    /// <summary>
    /// Resolves the picker's selected index (into <see cref="BuildLabels"/>'s
    /// output) against <paramref name="existing"/> into the reminder set to
    /// persist for <paramref name="eventId"/>.
    ///
    /// Preservation: if <paramref name="existing"/> is not representable and
    /// the sentinel entry is still selected, the existing set is returned
    /// verbatim (same Ids, same offsets) — the user never touched the
    /// picker, so nothing is destroyed. Selecting any other entry replaces
    /// the whole set with the single chosen preset (or empties it for "No
    /// reminder"), which is what the 0..1 editor model has always promised.
    ///
    /// Identity rule (unchanged from the representable case): a chosen
    /// offset equal to the sole existing reminder's offset preserves that
    /// reminder's Id; any other choice mints a new Id. See REMINDERS.md
    /// "Reminder identity across saves".
    /// </summary>
    public static List<Reminder> ResolveForSave(
        IReadOnlyList<Reminder> existing, int selectedIndex, Guid eventId)
    {
        if (!IsRepresentable(existing))
        {
            if (selectedIndex == 0)
                return existing.ToList(); // sentinel still selected: untouched, preserve verbatim.

            return BuildSingleOrEmpty(Presets[selectedIndex - 1], eventId, existingForIdentity: null);
        }

        var existingSingle = existing.Count == 1 ? existing[0] : null;
        return BuildSingleOrEmpty(Presets[selectedIndex], eventId, existingSingle);
    }

    private static List<Reminder> BuildSingleOrEmpty(
        ReminderPreset preset, Guid eventId, Reminder? existingForIdentity)
    {
        if (preset.Quantity is not int quantity)
            return new List<Reminder>();

        var offsetUnchanged =
            existingForIdentity is not null
            && existingForIdentity.OffsetQuantity == quantity
            && existingForIdentity.OffsetUnit == preset.Unit;

        return new List<Reminder>
        {
            new Reminder
            {
                Id = offsetUnchanged ? existingForIdentity!.Id : Guid.NewGuid(),
                EventId = eventId,
                OffsetQuantity = quantity,
                OffsetUnit = preset.Unit,
            }
        };
    }
}
