using Chronicle.Projection;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Chronicle.Notifications;

/// <summary>
/// The seam between Chronicle's reminder projection and the OS notification
/// scheduler. The single method takes the FULLY PROJECTED desired set and is
/// responsible only for making the OS schedule match it.
///
/// The scheduler knows nothing of events, recurrence rules, repositories, or
/// expansion — all of that is upstream in
/// <see cref="EventProjection.ReminderSchedule"/>, which produces the
/// <see cref="ReminderOccurrence"/> list handed here. <c>ReminderOccurrence</c>
/// is the boundary object; the pipeline is
/// Event + Reminder → ReminderSchedule → ReminderOccurrence[] →
/// IReminderScheduler → OS scheduled toasts.
///
/// This seam is where the notification subsystem owns its platform APIs. The
/// point is not to hide Windows — Chronicle is a native Windows app and uses
/// Windows APIs freely where they belong. The point is *responsibility*: only
/// this subsystem's implementation touches the OS scheduler, notification
/// concepts never leak back into the reminder domain model, and no unrelated
/// code manipulates scheduled toasts.
///
/// Contract:
/// <list type="bullet">
///   <item><b>Whole-set reconcile.</b> The implementation converges the OS
///   schedule to <paramref name="desired"/> — it does not grow per-item
///   Schedule / Cancel / Update operations.</item>
///   <item><b>Sole owner of the OS cache.</b> No other code path adds,
///   removes, or updates scheduled toasts. The projection is the source of
///   truth; the OS list is a disposable cache each reconcile rebuilds.</item>
///   <item><b>Bounded.</b> Reconcile is subject to an implementation-defined
///   maximum, to avoid exhausting OS scheduling limits. When
///   <paramref name="desired"/> exceeds that maximum, the earliest-firing
///   reminders are preferred (the input is fire-time-ordered).</item>
///   <item><b>Failures surface to the caller.</b> They are not swallowed
///   here, so the reconcile orchestration can log them and a future
///   diagnostics/status surface has somewhere to observe them.</item>
/// </list>
/// </summary>
internal interface IReminderScheduler
{
    /// <param name="desired">
    /// The fully projected reminder set, ordered by fire time. The
    /// implementation makes the OS schedule match this, subject to the bound
    /// above.
    /// </param>
    Task ReconcileAsync(IReadOnlyList<ReminderOccurrence> desired);
}
