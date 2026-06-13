using System;

namespace Chronicle.Helpers;

/// <summary>
/// Shared hex-color parsing for sidebar and calendar grid rendering.
/// </summary>
internal static class ColorHelper
{
    public const string AppAccentHex = "#3B82F6";

    public static Windows.UI.Color AppAccent { get; } =
        new() { A = 255, R = 59, G = 130, B = 246 };

    /// <summary>
    /// Preset palette offered when creating/editing a calendar. Calendars
    /// still store an arbitrary "#RRGGBB" string; this is just the curated
    /// set surfaced in the UI so we don't need a full color-picker control.
    /// </summary>
    public static readonly string[] Palette =
    {
        "#3B82F6", // blue
        "#EF4444", // red
        "#10B981", // green
        "#F59E0B", // amber
        "#8B5CF6", // violet
        "#EC4899", // pink
        "#14B8A6", // teal
        "#6B7280", // gray
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
}
