namespace Uts.Api.Domain;

public record Client(
    long Id,
    string Name,
    string? Contact,
    string? TelegramChatId,
    string? Notes,
    DateTime CreatedAt);

public record City(
    string Code,
    string Name,
    bool Active);

public record Trip(
    long Id,
    string Title,
    string? OriginCode,
    string? DestinationCode,
    DateTime? PlannedDate,
    string Status,
    string? Notes,
    DateTime CreatedAt,
    string TransportMode,
    long? CourierClientId = null,
    string? CourierName = null);

public record TripListItem(
    long Id,
    string Title,
    string? OriginCode,
    string? DestinationCode,
    DateTime? PlannedDate,
    string Status,
    string? Notes,
    DateTime CreatedAt,
    long ShipmentCount,
    string[] Stops,
    string TransportMode,
    long? CourierClientId = null,
    string? CourierName = null);

/// <summary>Recurring-route preset so regular round-trips can be spawned
/// (optionally with the mirrored return leg) with one click.</summary>
public record TripTemplate(
    long Id,
    string Title,
    string? OriginCode,
    string? DestinationCode,
    string[] Stops,
    string TransportMode,
    DateTime CreatedAt);

public record ShipmentEvent(
    long Id,
    long ShipmentId,
    string Type,
    string Message,
    string Source,
    DateTime CreatedAt);

/// <summary>Row shown in the admin shipments list.</summary>
public record ShipmentListItem(
    long Id,
    string TrackingNumber,
    string Status,
    string TransportMode,
    string? OriginCode,
    string DestinationCode,
    string DestinationName,
    string SenderName,
    string RecipientName,
    string Description,
    long? TripId,
    string? TripTitle,
    DateTime? TripPlannedDate,
    DateTime CreatedAt);

/// <summary>Full shipment as returned to the admin panel.</summary>
public record ShipmentDetail(
    long Id,
    string TrackingNumber,
    string Status,
    long SenderId,
    string SenderName,
    long RecipientId,
    string RecipientName,
    string? OriginCode,
    string? OriginName,
    string DestinationCode,
    string DestinationName,
    string Description,
    string TransportMode,
    string? Notes,
    string Pin,
    long? TripId,
    string? TripTitle,
    DateTime? TripPlannedDate,
    string? TripStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? DeliveredAt,
    ShipmentEvent[] Events);

/// <summary>Public (PIN-locked) tracking payload.</summary>
public record TrackPublic(
    string Number,
    string Status,
    string? OriginCode,
    string DestinationCode,
    string DestinationName,
    DateTime CreatedAt,
    DateTime? DeliveredAt,
    bool UnlockRequired,
    TrackEvent[] Events);

/// <summary>Tracking payload after a correct access code was supplied.</summary>
public record TrackFull(
    string Number,
    string Status,
    string? OriginCode,
    string? OriginName,
    string DestinationCode,
    string DestinationName,
    DateTime CreatedAt,
    DateTime? DeliveredAt,
    string SenderName,
    string RecipientName,
    string Description,
    string TransportMode,
    string? Notes,
    string? TripTitle,
    DateTime? TripPlannedDate,
    string? TelegramClaimUrl,
    bool UnlockRequired,
    TrackEvent[] Events);

public record TrackEvent(
    string Type,
    string Message,
    DateTime CreatedAt);
