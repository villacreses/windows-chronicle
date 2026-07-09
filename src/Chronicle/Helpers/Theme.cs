namespace Chronicle.Helpers;

/// <summary>
/// Central dark "Fluent" palette for Chronicle, ported from the design
/// handoff (dark surfaces + teal-green accent). These tokens are the single
/// source of truth for colors the code-based renderers apply directly; the
/// standard WinUI controls (dialogs, buttons, popover) pick up the matching
/// theme via App.xaml (RequestedTheme=Dark + SystemAccentColor overrides).
///
/// Text levels use white at varying opacity so they read correctly over the
/// dark surfaces, mirroring the design's rgba(255,255,255,a) ramp.
/// </summary>
internal static class Theme
{
    private static Windows.UI.Color Rgb(byte r, byte g, byte b) =>
        new() { A = 255, R = r, G = g, B = b };

    private static Windows.UI.Color White(double opacity) =>
        new() { A = (byte)(opacity * 255), R = 255, G = 255, B = 255 };

    // ── Accent (teal-green) ───────────────────────────────────────────────
    public static readonly Windows.UI.Color Accent = Rgb(0x2E, 0x9B, 0x7C);
    public static readonly Windows.UI.Color AccentHover = Rgb(0x38, 0xAD, 0x8B);
    public static readonly Windows.UI.Color AccentStrong = Rgb(0x1A, 0x7F, 0x64);
    public static readonly Windows.UI.Color AccentText = Rgb(0x5B, 0xC8, 0xA4);
    public static readonly Windows.UI.Color OnAccent = Rgb(0x04, 0x13, 0x0D);

    /// <summary>accent at ~10% — selected day-cell tint.</summary>
    public static readonly Windows.UI.Color AccentSoft =
        new() { A = 26, R = 0x2E, G = 0x9B, B = 0x7C };

    // ── Density ramp (Year View day-cell tint) ────────────────────────────
    // Three tiers of accent-with-opacity for the 1–2 / 3–5 / 6+ event
    // buckets. Kept next to AccentSoft so the whole ramp is one place.
    public static readonly Windows.UI.Color AccentDensity1 =
        new() { A = 38,  R = 0x2E, G = 0x9B, B = 0x7C };
    public static readonly Windows.UI.Color AccentDensity2 =
        new() { A = 90,  R = 0x2E, G = 0x9B, B = 0x7C };
    public static readonly Windows.UI.Color AccentDensity3 =
        new() { A = 153, R = 0x2E, G = 0x9B, B = 0x7C };

    // ── Neutral surfaces (Mica-dark) ──────────────────────────────────────
    public static readonly Windows.UI.Color Window = Rgb(0x1C, 0x1D, 0x20);
    public static readonly Windows.UI.Color Sidebar = Rgb(0x23, 0x24, 0x27);
    public static readonly Windows.UI.Color Content = Rgb(0x1C, 0x1D, 0x20);
    public static readonly Windows.UI.Color Cell = Rgb(0x20, 0x21, 0x25);
    public static readonly Windows.UI.Color CellOut = Rgb(0x1B, 0x1C, 0x1F);
    public static readonly Windows.UI.Color Elevated = Rgb(0x2A, 0x2B, 0x2F);

    // ── Lines / strokes (white at low opacity) ────────────────────────────
    public static readonly Windows.UI.Color Hairline = White(0.06);
    public static readonly Windows.UI.Color Hairline2 = White(0.09);
    public static readonly Windows.UI.Color Stroke = White(0.12);

    // ── Text ──────────────────────────────────────────────────────────────
    public static readonly Windows.UI.Color Text = White(0.95);
    public static readonly Windows.UI.Color Text2 = White(0.62);
    public static readonly Windows.UI.Color Text3 = White(0.40);
    public static readonly Windows.UI.Color Text4 = White(0.28);

    public static readonly Windows.UI.Color Danger = Rgb(0xF0, 0x68, 0x6B);
}
