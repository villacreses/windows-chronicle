using Chronicle.Models;
using Microsoft.UI.Xaml;
using System;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Single seam between the calendar renderers (CalendarGrid, Week, Day,
/// MiniMonth, SelectedDay) and their host. Replaces the per-Render
/// <c>Action&lt;...&gt;</c> parameters that were threaded through every
/// renderer, helper, and internal builder. The renderer takes one
/// reference at construction and calls these methods directly — no
/// per-render delegate allocations, no signature churn when a new
/// interaction is added.
///
/// The interface is intentionally a flat list of intent-named methods
/// rather than a generic event bus: it's a typed contract, easy to
/// search, and trivially trimmable by AOT. It is not MVVM, not DI, and
/// not a reactive bus — see "Avoid Premature MVVM" in DECISIONS.md.
/// </summary>
internal interface ICalendarInteractionHost
{
    // ── Day selection ─────────────────────────────────────────────────
    //
    // OnDaySelected vs OnMiniMonthDateSelected: the mini-month routes
    // through a separate method because a tap on a leading/trailing day
    // belongs to a different month and the host has to re-anchor
    // _displayMonth before refreshing. The main grid and week-header
    // taps never cross months that way, so they use the simpler path.

    void OnDaySelected(DateTime date);
    void OnMiniMonthDateSelected(DateTime date);
    void OnMiniMonthPrevMonth();
    void OnMiniMonthNextMonth();

    /// <summary>
    /// Year View day-cell tap: drill from Year to Month at the tapped day.
    /// Distinct from <see cref="OnDaySelected"/> because it also switches
    /// the active view — Year is a top-down overview, its cells always
    /// mean "take me there," not "focus this day in place."
    /// </summary>
    void OnYearDaySelected(DateTime date);

    // ── Empty-space activation (creates an event) ─────────────────────

    /// <summary>Month View: tap on empty cell space.</summary>
    void OnDayCreateRequested(DateTime dayDate);

    /// <summary>Week / Day View: tap on an empty timeline slot.</summary>
    void OnTimeSlotCreateRequested(DateTime dayDate, TimeSpan startTime);

    // ── Event interaction ─────────────────────────────────────────────
    //
    // Two methods because the behaviors differ: chips (Month/Week/Day)
    // open the read-only popover anchored to the chip; selected-day
    // panel rows open the edit popover directly. Both go through the
    // host so the popover and edit-flyout positioning stays in one place.

    /// <summary>Event chip tap (Month/Week/Day): show read-only popover anchored to <paramref name="anchor"/>.</summary>
    void OnEventClicked(Event evt, FrameworkElement anchor);

    /// <summary>
    /// Selected-day panel row click: open edit popover directly, anchored to
    /// <paramref name="anchor"/> (the clicked row) so the editor reads as a
    /// talk-bubble from the row — same pattern as
    /// <see cref="OnEventClicked"/>.
    /// </summary>
    void OnEventActivated(Event evt, FrameworkElement anchor);
}

/// <summary>
/// Seam between <see cref="SidebarRenderer"/> and its host. Kept separate
/// from <see cref="ICalendarInteractionHost"/> because the concerns are
/// orthogonal — a future compact-mode or popup calendar picker could
/// reuse the calendar renderers without implementing sidebar management.
/// </summary>
internal interface ISidebarHost
{
    void OnCalendarVisibilityToggled(Guid calendarId, bool isVisible);
    void OnAddCalendar();
    void OnEditCalendar(Calendar calendar);
    void OnDeleteCalendar(Calendar calendar);
}
