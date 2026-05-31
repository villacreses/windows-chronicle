using System;

namespace Chronicle.Models;

public sealed class Calendar
{
    public Guid Id { get; init; }
    public string Name { get; set; } = ""; // Store as hex string for simplicity
    public string Color { get; set; } = "#3B82F6";
}
