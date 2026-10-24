namespace Uts.Api.Services;

/// <summary>Polish user-facing texts for notifications and bot replies. All
/// informal "ty" form, courier persona ("I am travelling / I arrived").</summary>
public static class TelegramTexts
{
    public const string Help =
        "UTS — powiadomienia o paczkach 📦\n\n" +
        "/start <kod> — powiąż ten czat ze swoimi paczkami (kod dostaniesz na stronie śledzenia lub od kuriera)\n" +
        "/notifications — wyłącz lub włącz powiadomienia 🔔/🔕\n\n" +
        "Kurier ma też komendy operacyjne: /leaving, /arrived, /deliver, /packlist";

    public const string NoBinding =
        "Ten czat nie jest jeszcze powiązany z żadnym odbiorcą.\n" +
        "Napisz /start <kod> — kod dostaniesz na stronie śledzenia po odblokowaniu paczki, albo od kuriera.";

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

    /// <summary>Single-shipment status push (recipient).</summary>
    public static string ShipmentStatus(string number, string status, string? destinationName) => status switch
    {
        "registered"     => $"Masz nową paczkę: {number} → {destinationName ?? "?"}.",
        "picked_up"     => $"Paczka {number} została odebrana 📦",
        "in_transit"    => $"Paczka {number} jest w drodze 🚆",
        "at_destination" => $"Kurier dotarł do {destinationName ?? "miasta docelowego"} 📍 Dostawa zostanie umówiona wkrótce.",
        "delivered"     => $"Paczka {number} dostarczona ✅",
        "cancelled"     => $"Paczka {number} została anulowana ❌",
        "lost"          => $"Niestety paczka {number} się nie odnalazła 😞 Przepraszamy.",
        _               => $"Paczka {number}: {StatusLabel(status)}.",
    };

    public static string TripDeparture(IEnumerable<string> numbers)
        => $"Wyruszam w trasę 🚆 Twoje paczki w tej podróży: {Join(numbers)}.";

    public static string TripArrival(string? city, IEnumerable<string> numbers)
        => $"Dotarłem do {city ?? "miasta docelowego"} 📍 Umówimy dostawę Twoich paczek: {Join(numbers)}.";

    public static string HelpCourier()
        => "Komendy kuriera:\n" +
           "/leaving — oznacz wszystkie paczki z najbliższej podróży jako w drodze\n" +
           "/arrived — oznacz wszystkie jako dotarłe do miasta docelowego (podróż = dokończona)\n" +
           "/deliver — dostarczaj paczki pojedynczo\n" +
           "/packlist — wykaz paczek najbliższej podróży";

    private static string Join(IEnumerable<string> numbers) => string.Join(", ", numbers);
}
