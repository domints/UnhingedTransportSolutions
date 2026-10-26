namespace Uts.Api.Services;

/// <summary>Polish user-facing texts for notifications and bot replies. All
/// informal "ty" form, courier persona ("I am travelling / I arrived").</summary>
public static class TelegramTexts
{
    public const string Help =
        "UTS - powiadomienia o paczkach 📦\n\n" +
        "/start <kod> - powiąż ten czat ze swoimi paczkami (kod dostaniesz na stronie śledzenia lub od kuriera)\n" +
        "/notifications - wyłącz lub włącz powiadomienia 🔔/🔕\n\n" +
        "Kurier ma też komendy operacyjne: /leaving, /arrived, /deliver, /packlist";

    public const string NoBinding =
        "Ten czat nie jest jeszcze powiązany z żadnym odbiorcą.\n" +
        "Napisz /start <kod> - kod dostaniesz na stronie śledzenia po odblokowaniu paczki, albo od kuriera.";

    public const string ClaimOk =
        "Powiadomienia aktywne ✅ Będę informował Cię o paczkach adresowanych do Ciebie.";

    public const string ClaimInvalid =
        "Nie rozpoznaję tego kodu albo już wygasł. Poproś o nowy link do powiadomień.";

    public const string CourierOnly =
        "Ta komenda jest zarezerwowana dla kuriera. 😉";

    public static string StatusLabel(string status) => status switch
    {
        "registered"    => "Zarejestrowana",
        "picked_up"     => "Odebrana",
        "in_transit"    => "W drodze",
        "at_destination" => "Dotarła do miasta docelowego",
        "delivered"     => "Dostarczona",
        "cancelled"     => "Anulowana",
        "lost"          => "Zaginiona",
        _               => status,
    };

    /// <summary>Single-shipment status push (recipient). Movements that involve a
    /// courier name the person the parcel travelled with, when we know it.</summary>
    public static string ShipmentStatus(string number, string status, string? destinationName, string? courierName = null) => status switch
    {
        "registered"     => $"Masz nową paczkę: {number} → {destinationName ?? "?"}.",
        "picked_up"     => $"Paczka {number} została odebrana 📦",
        "in_transit"    => courierName is null
            ? $"Paczka {number} jest w drodze 🚆"
            : $"Paczka {number} jest w drodze z kurierem {courierName} 🚆",
        "at_destination" => $"{Courier(courierName)} dotarł do {destinationName ?? "miasta docelowego"} 📍 Dostawa zostanie umówiona wkrótce.",
        "delivered"     => courierName is null
            ? $"Paczka {number} dostarczona ✅"
            : $"{Courier(courierName)} dostarczył paczkę {number} ✅",
        "cancelled"     => $"Paczka {number} została anulowana ❌",
        "lost"          => $"Niestety paczka {number} się nie odnalazła 😞 Przepraszamy.",
        _               => $"Paczka {number}: {StatusLabel(status)}.",
    };

    public static string TripDeparture(string? courierName, IEnumerable<string> numbers)
        => $"{Courier(courierName)} wyrusza w trasę 🚆 Twoje paczki w tej podróży: {Join(numbers)}.";

    public static string TripArrival(string? courierName, string? city, IEnumerable<string> numbers)
        => $"{Courier(courierName)} dotarł do {city ?? "miasta docelowego"} 📍 Umówmy dostawę Twoich paczek: {Join(numbers)}.";

    /// <summary>"Kurier Marek" as a sentence subject; a joint name when the trip has no assigned courier.</summary>
    private static string Courier(string? name)
        => string.IsNullOrWhiteSpace(name) ? "Kurier" : $"Kurier {name}";

    /// <summary>Internal notes changed — recipient gets the new content
    /// straight into the chat (only for bound + unmuted chats).</summary>
    public static string NotesUpdated(string number, string content)
        => $"Do paczki {number} dodano nową informację 📝\n{content}";

    public static string NotesCleared(string number)
        => $"Informacja o paczce {number} została usunięta 📝";

    public static string HelpCourier()
        => "Komendy kuriera:\n" +
           "/leaving — oznacz wszystkie paczki z najbliższej podróży jako w drodze\n" +
           "/arrived — oznacz wszystkie jako dotarłe do miasta docelowego (podróż = dokończona)\n" +
           "/deliver — dostarczaj paczki pojedynczo\n" +
           "/packlist — wykaz paczek najbliższej podróży";

    private static string Join(IEnumerable<string> numbers) => string.Join(", ", numbers);
}
