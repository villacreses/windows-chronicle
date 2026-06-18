using Chronicle.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Chronicle.Views.Rendering;

/// <summary>
/// Per-chip tap payload stored in <see cref="FrameworkElement.Tag"/> on every
/// event chip and timeline event block. Carries the
/// <see cref="Models.Event"/> the visual represents and the
/// <see cref="ICalendarInteractionHost"/> to dispatch to.
///
/// Why this exists: tap routing for event visuals used to require either a
/// per-chip closure (capturing both <c>evt</c> and the host) or a renderer-
/// cached <c>TappedEventHandler</c> threaded through every helper signature.
/// The first allocated per chip per render; the second prop-drilled a
/// parameter through the same chain the
/// <see cref="ICalendarInteractionHost"/> seam was introduced to flatten.
///
/// Putting both references on the visual itself lets the static helpers
/// (<see cref="CalendarRenderHelper.CreateEventChip"/>,
/// <see cref="TimelineRenderHelper"/>) attach a single class-level static
/// handler that reads everything from <c>sender</c>. Helper signatures drop
/// the tap-handler parameter entirely; per-chip cost is one
/// <c>EventTapTarget</c> allocation (the same shape as the closure it
/// replaces, but a single named type rather than a unique compiler-
/// generated closure per call site — friendlier to trim/AOT).
///
/// <see cref="Border"/> is sealed in WinUI 3, so a subclass that carries
/// these fields directly is not an option.
/// </summary>
internal sealed record EventTapTarget(Event Event, ICalendarInteractionHost Host)
{
    /// <summary>
    /// Class-level tap handler shared by every event chip and timeline
    /// block. Static, capture-free, allocated once at class init.
    /// </summary>
    public static readonly TappedEventHandler OnTapped = (s, e) =>
    {
        e.Handled = true;
        var fe = (FrameworkElement)s;
        var target = (EventTapTarget)fe.Tag;
        target.Host.OnEventClicked(target.Event, fe);
    };

    /// <summary>
    /// Class-level double-tap consumer. Stops double-taps on chips/blocks
    /// from triggering the surrounding cell's create-event behavior.
    /// </summary>
    public static readonly DoubleTappedEventHandler MarkHandled = (s, e) => e.Handled = true;
}
