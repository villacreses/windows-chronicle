using System;

namespace Chronicle.Models;

public sealed class Calendar
{
    /// <summary>
    /// Default calendar color (teal accent) as a "#RRGGBB" hex string.
    /// Lives in the domain so the model carries no dependency on the
    /// UI-layer ColorHelper; the app converts hex to a platform color at
    /// render time.
    /// </summary>
    public const string DefaultColorHex = "#2E9B7C";

    public Guid Id { get; init; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = DefaultColorHex; // "#RRGGBB"
}
