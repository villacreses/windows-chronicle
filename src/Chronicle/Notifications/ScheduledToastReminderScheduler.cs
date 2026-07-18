using Chronicle.Projection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Chronicle.Notifications;

/// <summary>
/// The one class that talks to <c>Windows.UI.Notifications</c>. Implements the
/// reconcile contract via classic scheduled toasts (the mechanism validated
/// by the Phase C activation spike): the OS delivers them even when Chronicle
/// is closed, with no in-app timer.
///
/// Reconcile is clear-and-rebuild over a single reserved group. This is
/// required, not merely convenient: <c>AddToSchedule</c> is purely additive
/// (it does not dedupe on tag/id — a spike finding), so stale toasts must be
/// removed explicitly. Owning the whole group makes "remove all, add desired"
/// the convergence step.
///
/// Failures (e.g. the platform quota) propagate to the caller; this class
/// does not swallow them. The reconcile orchestration decides policy (log,
/// keep the app usable, repair on the next reconcile).
/// </summary>
internal sealed class ScheduledToastReminderScheduler : IReminderScheduler
{
    // One reserved OS group; its entire contents are this scheduler's
    // disposable cache (see REMINDERS.md).
    private const string Group = "chronicle-reminders";

    // The IReminderScheduler contract's "bounded" clause, realized: an
    // implementation-defined maximum well under the platform's ~4096
    // scheduled-toast limit. The 60-day horizon keeps realistic datasets far
    // below this; the cap only guards a pathological set. The input is
    // fire-time-ordered, so honoring the cap keeps the earliest reminders.
    private const int MaxScheduled = 1000;

    public Task ReconcileAsync(IReadOnlyList<ReminderOccurrence> desired)
    {
        var notifier = ToastNotificationManager.CreateToastNotifier();

        foreach (var scheduled in notifier.GetScheduledToastNotifications())
        {
            if (scheduled.Group == Group)
                notifier.RemoveFromSchedule(scheduled);
        }

        var now = DateTimeOffset.Now;
        var added = 0;
        foreach (var reminder in desired)
        {
            if (added >= MaxScheduled)
                break;

            var deliverAt = new DateTimeOffset(reminder.FireTimeUtc, TimeSpan.Zero);
            // The projection window already starts at "now"; this guards the
            // sub-second race between projecting and scheduling. Windows
            // rejects (or immediately fires) past deliveries.
            if (deliverAt <= now)
                continue;

            notifier.AddToSchedule(BuildToast(reminder, deliverAt));
            added++;
        }

        return Task.CompletedTask;
    }

    private static ScheduledToastNotification BuildToast(
        ReminderOccurrence reminder, DateTimeOffset deliverAt)
    {
        // Identity travels in the launch arguments (unbounded); the tag is a
        // length-capped bookkeeping hash only.
        var payload = ReminderActivationPayload.Encode(reminder.Ref, reminder.ReminderId);
        var whenLocal = reminder.EventStartTimeUtc.ToLocalTime();
        var body = whenLocal.ToString("ddd, MMM d · h:mm tt");

        var xml =
            $"<toast launch=\"{Escape(payload)}\" activationType=\"foreground\">"
            + "<visual><binding template=\"ToastGeneric\">"
            + $"<text>{Escape(reminder.Title)}</text>"
            + $"<text>{Escape(body)}</text>"
            + "</binding></visual></toast>";

        var doc = new XmlDocument();
        doc.LoadXml(xml);

        return new ScheduledToastNotification(doc, deliverAt)
        {
            Group = Group,
            Tag = ShortTag(payload),
        };
    }

    // FNV-1a over the payload → 8 hex chars. Bookkeeping only: removal is by
    // Group, so tag collisions are harmless.
    private static string ShortTag(string payload)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in payload) { hash ^= c; hash *= 16777619; }
            return hash.ToString("x8");
        }
    }

    private static string Escape(string s) => s
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
}
