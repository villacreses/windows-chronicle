using Chronicle.Models.Recurrence;
using System;
using System.Globalization;

namespace Chronicle.Projection;

/// <summary>
/// The wire format for a scheduled reminder's identity as it travels in a
/// toast's launch arguments — written by the notification scheduler (Unit 3)
/// and read back by activation handling (Unit 4). This is pure identity
/// serialization, not a Windows API concern, so it lives in Core where both
/// sides can share one canonical format and it can be unit-tested.
///
/// A scheduled toast is identified by <c>(EventRef, ReminderId)</c> — the
/// occurrence it belongs to plus which reminder on that occurrence (an
/// occurrence may carry several). The launch arguments carry the full
/// identity; the OS tag is only a bookkeeping hash (length-capped), so the
/// full value must round-trip through here, not through the tag.
///
/// Format (pipe-delimited, no user content — identity only):
/// <list type="bullet">
///   <item><c>m|{eventId}|{reminderId}</c> — a standalone/master event.</item>
///   <item><c>o|{seriesId}|{anchorTicksUtc}|{reminderId}</c> — a recurring
///   occurrence, keyed by its rule-walk anchor (stored as UTC ticks so it
///   round-trips exactly).</item>
/// </list>
/// </summary>
public static class ReminderActivationPayload
{
    public static string Encode(EventRef eventRef, Guid reminderId) => eventRef switch
    {
        EventRef.Master m =>
            $"m|{m.Id}|{reminderId}",
        EventRef.Occurrence o =>
            $"o|{o.SeriesId}|{o.AnchorUtc.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}|{reminderId}",
        _ => throw new InvalidOperationException(
            $"Unknown EventRef variant '{eventRef.GetType().Name}'."),
    };

    /// <summary>
    /// Parses a payload produced by <see cref="Encode"/>. Returns null for
    /// anything malformed — activation must degrade gracefully (open the app)
    /// rather than throw on an unrecognized or corrupted argument.
    /// </summary>
    public static (EventRef Ref, Guid ReminderId)? TryDecode(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        var parts = payload.Split('|');
        try
        {
            switch (parts[0])
            {
                case "m" when parts.Length == 3:
                    return (new EventRef.Master(Guid.Parse(parts[1])),
                            Guid.Parse(parts[2]));

                case "o" when parts.Length == 4:
                    var seriesId = Guid.Parse(parts[1]);
                    var anchor = new DateTime(
                        long.Parse(parts[2], CultureInfo.InvariantCulture),
                        DateTimeKind.Utc);
                    return (new EventRef.Occurrence(seriesId, anchor),
                            Guid.Parse(parts[3]));

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is FormatException or OverflowException
                                or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
