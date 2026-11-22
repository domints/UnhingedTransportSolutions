namespace Uts.Api.Domain;

/// <summary>Courier status flow + the allowed transition graph.</summary>
public static class ShipmentStatus
{
    public const string Registered     = "registered";
    public const string PickedUp       = "picked_up";
    public const string InTransit      = "in_transit";
    public const string AtDestination  = "at_destination";
    public const string Delivered      = "delivered";
    public const string Cancelled      = "cancelled";
    public const string Lost           = "lost";

    public static readonly string[] All =
        [Registered, PickedUp, InTransit, AtDestination, Delivered, Cancelled, Lost];

    public static readonly Dictionary<string, string> Labels = new()
    {
        [Registered]     = "Registered",
        [PickedUp]       = "Picked up",
        [InTransit]      = "In transit",
        [AtDestination]  = "At destination",
        [Delivered]      = "Delivered",
        [Cancelled]      = "Cancelled",
        [Lost]           = "Lost",
    };

    public static readonly Dictionary<string, string[]> Transitions = new()
    {
        [Registered]    = [PickedUp, Cancelled],
        // Back-edges are the one-step "undo" of the main path (see BackTransitions):
        // a mis-click in the panel must be fixable without a psql session.
        [PickedUp]      = [InTransit, Registered, Cancelled, Lost],
        [InTransit]     = [AtDestination, PickedUp, Delivered, Lost],
        [AtDestination] = [Delivered, InTransit, Lost],
        [Delivered]     = [AtDestination],
        [Cancelled]     = [],
        [Lost]          = [InTransit], // found parcel
    };

    /// <summary>The single backward step allowed from each status. <c>cancelled</c>
    /// stays terminal; <c>lost → in_transit</c> is the "found it again" path.</summary>
    public static readonly Dictionary<string, string> BackTransitions = new()
    {
        [PickedUp]      = Registered,
        [InTransit]     = PickedUp,
        [AtDestination] = InTransit,
        [Delivered]     = AtDestination,
        [Lost]          = InTransit,
    };

    public static bool CanTransition(string from, string to)
        => Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>True when <paramref name="to"/> is the undo step of <paramref name="from"/>.</summary>
    public static bool IsBackward(string from, string to)
        => BackTransitions.TryGetValue(from, out var back) && back == to;

    /// <summary>The one-step-back status, or null when the parcel cannot go back.</summary>
    public static string? PreviousStatus(string from)
        => BackTransitions.GetValueOrDefault(from);

    public static string Label(string status)
        => Labels.GetValueOrDefault(status, status);
}
