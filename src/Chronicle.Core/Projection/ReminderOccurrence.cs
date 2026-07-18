using Chronicle.Models.Recurrence;
using System;

namespace Chronicle.Projection;

/// <summary>
/// One scheduled-notification intent emitted by
/// <see cref="EventProjection.ReminderSchedule"/> — a single reminder on a
/// single occurrence, ready to be registered with the OS notification
/// scheduler by the reconciler.
///
/// Identity is <c>(Ref, ReminderId)</c>:
/// <list type="bullet">
///   <item><see cref="Ref"/> addresses the occurrence — a standalone event
///   yields <see cref="EventRef.Master"/>, a recurring instance yields
///   <see cref="EventRef.Occurrence"/> keyed by the rule-walk anchor, so it
///   is stable across reloads and rule-version changes.</item>
///   <item><see cref="ReminderId"/> discriminates <em>which</em> reminder on
///   that occurrence, because one occurrence may carry several. A scheduled
///   toast is a per-reminder, per-occurrence intent.</item>
/// </list>
/// The full identity travels in the toast's launch arguments; the short OS
/// tag/group is only for group-clear (see REMINDERS.md).
/// </summary>
public sealed record ReminderOccurrence(
    EventRef Ref,
    Guid ReminderId,
    DateTime FireTimeUtc,
    DateTime EventStartTimeUtc,
    string Title);
