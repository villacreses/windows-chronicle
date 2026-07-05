using System;

namespace Chronicle.Models.Recurrence;

/// <summary>
/// Write-boundary normalization for a recurring series' anchor timezone.
/// Extracted from <c>EventEditPopover.GetDefaultRecurringTimeZoneId</c> so the
/// "persisted TimeZoneId is always IANA" invariant (RECURRENCE.md invariant #7
/// / "Anchor Zone Is Authoritative") is testable independent of WinUI.
/// </summary>
internal static class RecurrenceTimeZone
{
    /// <summary>
    /// Normalizes a system zone id to IANA. On Windows the local id is a
    /// Windows id (e.g. "Pacific Standard Time"), converted via
    /// <c>TryConvertWindowsIdToIanaId</c>. An already-IANA id is verified via
    /// the reverse conversion and returned as-is. If it maps to neither form,
    /// degrades to "UTC" (with a debug log) rather than persisting an unmapped
    /// string — the escape hatch can only affect that one zone's experience,
    /// never weaken the IANA invariant.
    /// </summary>
    public static string NormalizeToIana(string localZoneId)
    {
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(localZoneId, out var iana))
            return iana;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(localZoneId, out _))
            return localZoneId;

        System.Diagnostics.Debug.WriteLine(
            $"Local timezone '{localZoneId}' resolves to neither IANA nor "
            + "Windows; falling back to UTC for new recurring events.");
        return "UTC";
    }
}
