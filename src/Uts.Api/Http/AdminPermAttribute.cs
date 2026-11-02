namespace Uts.Api.Http;

/// <summary>
/// The eight grantable permission names. <c>keys.*</c> deliberately does not
/// exist — a key can never manage, mint or revoke keys, and never touches
/// identity endpoints; those stay OIDC-cookie-only.
/// </summary>
public static class Perm
{
    public const string ShipmentsRead = "shipments.read";
    public const string ShipmentsWrite = "shipments.write";
    public const string ClientsRead = "clients.read";
    public const string ClientsWrite = "clients.write";
    public const string TripsRead = "trips.read";
    public const string TripsWrite = "trips.write";
    public const string CitiesRead = "cities.read";
    public const string CitiesWrite = "cities.write";

    public static readonly string[] All =
    [
        ShipmentsRead, ShipmentsWrite,
        ClientsRead, ClientsWrite,
        TripsRead, TripsWrite,
        CitiesRead, CitiesWrite,
    ];
}

/// <summary>
/// Declares the permission an API key must hold to reach an admin endpoint.
/// Cookie admins (the uts-admins group) bypass permission checks entirely —
/// the attribute only gates key principals (see <see cref="AdminFilter"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AdminPermAttribute(string perm) : Attribute
{
    public string Perm { get; } = perm;
}

/// <summary>
/// Marker for admin endpoints that no key principal may ever call (key
/// management itself). Unlike <see cref="AdminPermAttribute"/> there is no
/// permission that satisfies it — the filter rejects keys outright.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AdminKeysOnlyAttribute : Attribute;
