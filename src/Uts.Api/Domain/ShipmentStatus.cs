namespace Uts.Api.Domain;

/// <summary>Courier status flow + the allowed transition graph.</summary>
public static class ShipmentStatus
{
    public const string Registered = "registered";
    public const string PickedUp   = "picked_up";
    public const string InTransit  = "in_transit";
    public const string Delivered  = "delivered";
    public const string Cancelled  = "cancelled";
    public const string Lost       = "lost";

    public static readonly string[] All =
        [Registered, PickedUp, InTransit, Delivered, Cancelled, Lost];

    public static readonly Dictionary<string, string> Labels = new()
    {
        [Registered] = "Registered",
        [PickedUp]   = "Picked up",
        [InTransit]  = "In transit",
        [Delivered]  = "Delivered",
        [Cancelled]  = "Cancelled",
        [Lost]       = "Lost",
    };

    public static readonly Dictionary<string, string[]> Transitions = new()
    {
        [Registered] = [PickedUp, Cancelled],
        [PickedUp]   = [InTransit, Cancelled, Lost],
        [InTransit]  = [Delivered, Lost],
        [Delivered]  = [],
        [Cancelled]  = [],
        [Lost]       = [],
    };

    public static bool CanTransition(string from, string to)
        => Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    public static string Label(string status)
        => Labels.GetValueOrDefault(status, status);
}
