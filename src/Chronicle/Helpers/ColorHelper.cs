using Chronicle.Models;
using System;
using System.Collections.Generic;

namespace Chronicle.Helpers;

/// <summary>
/// Shared hex-color parsing for sidebar and calendar grid rendering.
/// </summary>
internal static class ColorHelper
{
    // Single source of truth lives in the domain (Calendar.DefaultColorHex);
    // re-exposed here for UI call sites that default a calendar's color.
    public const string AppAccentHex = Calendar.DefaultColorHex;

    public static Windows.UI.Color AppAccent => Theme.Accent;

    /// <summary>
    /// Preset palette offered when creating/editing a calendar, tuned for the
    /// dark theme. Calendars still store an arbitrary "#RRGGBB" string; this is
    /// just the curated set surfaced in the UI so we don't need a full
    /// color-picker control.
    /// </summary>
    public static readonly string[] Palette =
    {
        "#5B92F5", // blue
        "#E0A458", // amber
        "#2E9B7C", // teal
        "#F0686B", // red
        "#8B5CF6", // violet
        "#EC4899", // pink
        "#10B981", // green
        "#9AA0AA", // gray
    };

    /// <summary>
    /// Returns a translucent "soft" fill of <paramref name="color"/> for event
    /// chip backgrounds over dark surfaces (the design's <c>--cal-soft</c>).
    /// </summary>
    public static Windows.UI.Color Soften(Windows.UI.Color color, double alpha = 0.18)
        => new() { A = (byte)(alpha * 255), R = color.R, G = color.G, B = color.B };

    /// <summary>
    /// Blends <paramref name="color"/> toward white so calendar-colored text
    /// stays legible on dark fills (the design's <c>--cal-text</c>).
    /// </summary>
    public static Windows.UI.Color LightenForText(Windows.UI.Color color, double amount = 0.5)
        => new()
        {
            A = 255,
            R = (byte)(color.R + (255 - color.R) * amount),
            G = (byte)(color.G + (255 - color.G) * amount),
            B = (byte)(color.B + (255 - color.B) * amount)
        };

    /// <summary>
    /// Parses a "#RRGGBB" hex color string into a <see cref="Windows.UI.Color"/>.
    /// Falls back to a neutral blue if the string is malformed.
    /// </summary>
    public static Windows.UI.Color ParseHexColor(string hex)
    {
        try
        {
            var s = hex.TrimStart('#');
            return new Windows.UI.Color
            {
                A = 255,
                R = Convert.ToByte(s[0..2], 16),
                G = Convert.ToByte(s[2..4], 16),
                B = Convert.ToByte(s[4..6], 16)
            };
        }
        catch
        {
            return AppAccent;
        }
    }

    /// <summary>
    /// Resolves a calendar id to its configured color, falling back to the
    /// app accent when the calendar is absent.
    /// </summary>
    public static Windows.UI.Color ResolveCalendarColor(
        IEnumerable<Calendar> calendars,
        Guid calendarId)
    {
        foreach (var calendar in calendars)
        {
            if (calendar.Id == calendarId)
                return ParseHexColor(calendar.Color);
        }

        return AppAccent;
    }
}
