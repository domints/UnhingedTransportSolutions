namespace Uts.Api.Services;

/// <summary>Polish user-facing texts for notifications and bot replies. All
/// informal "ty" form, courier persona ("I am travelling / I arrived").</summary>
public static class TelegramTexts
{
    public const string Help =
        "UTS - powiadomienia o paczkach 📦\n\n" +
        "/start <kod> - powiąż ten czat ze swoimi paczkami (kod dostaniesz na stronie śledzenia lub od kuriera)\n" +
        "/notifications - wyłącz lub włącz powiadomienia 🔔/🔕\n\n" +
        "Kurier ma też komendy operacyjne: /leaving, /arrived, /deliver, /packlist\n" +
        "Dostałeś paczkę do przekazania? /waiting pokaże, co masz w rękach.";

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

    /// <summary>Operational push to the courier who now holds the parcel.</summary>
    public static string HandoverToCourier(string number, string? destinationName)
        => $"📦 Masz paczkę {number} → {destinationName ?? "?"}.\n" +
           "Zawieź ją dalej albo przekaż dalej — /waiting pokaże, co masz w rękach.";

    /// <summary>Recipient-side note that the parcel changed hands.</summary>
    public static string HandoverRecipient(string number, string? courierName)
        => $"Paczka {number} jest teraz u {(string.IsNullOrWhiteSpace(courierName) ? "innego kuriera" : courierName)} 📦";

    /// <summary>Correction after a rollback out of 'delivered' (recipient saw the wrong push).</summary>
    public static string StatusCorrection(string number, string status)
        => $"Korekta statusu paczki {number}: {StatusLabel(status)}. Przepraszamy za zamieszanie.";

    /// <summary>Middleman's custody list — one message per parcel.</summary>
    public static string WaitingItem(string number, string status, string recipient, string destination)
        => $"📦 {number} → {destination}\nOdbiorca: {recipient}\nStatus: {StatusLabel(status)}";

    public const string WaitingEmpty =
        "Nie masz teraz żadnych paczek w pieczy. 📭";

    public const string CustodyConfirmed =
        "Potwierdzone ✅";

    public const string NotYourCustody =
        "Ta paczka nie jest w Twojej pieczy.";

    public const string ShipmentClosed =
        "Ta paczka jest już zamknięta (dostarczona, anulowana albo zaginiona).";

    public const string NotYourTrip =
        "To nie Twoja podróż.";

    public static string HelpCourier()
        => "Komendy kuriera:\n" +
           "/leaving — oznacz wszystkie paczki z Twojej najbliższej podróży jako w drodze\n" +
           "/arrived — oznacz wszystkie jako dotarłe do miasta docelowego (podróż = dokończona)\n" +
           "/deliver — dostarczaj paczki pojedynczo\n" +
           "/packlist — wykaz paczek Twojej najbliższej podróży\n" +
           "/waiting — paczki, które masz teraz w rękach";

    private static string Join(IEnumerable<string> numbers) => string.Join(", ", numbers);
}
