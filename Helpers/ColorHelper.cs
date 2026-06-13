using System;

namespace Chronicle.Helpers;

/// <summary>
/// Shared hex-color parsing for sidebar and calendar grid rendering.
/// </summary>
internal static class ColorHelper
{
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
            return new Windows.UI.Color { A = 255, R = 59, G = 130, B = 246 }; // #3B82F6
        }
    }
}
