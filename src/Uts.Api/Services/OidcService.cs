using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Uts.Api.Services;

/// <summary>
/// Hand-rolled OIDC authorization-code + PKCE flow against any standard-compliant
/// IdP (built for Authentik). Uses discovery, then authorize → token → userinfo.
/// Claims are taken from userinfo (falling back to the id_token payload).
/// </summary>
public sealed class OidcService(HttpClient http, IConfiguration cfg, ILogger<OidcService> log)
{
    private sealed record Discovery(string Authorization, string Token, string Userinfo);

    private sealed record Pending(string Nonce, string CodeVerifier, string CallbackUri, string RedirectAfter, DateTimeOffset CreatedAt);

    private volatile Discovery? _discovery;
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    private string Authority => (cfg["OIDC:Authority"] ?? "").TrimEnd('/');
    private string ClientId => cfg["OIDC:ClientId"] ?? "";
    private string ClientSecret => cfg["OIDC:ClientSecret"] ?? "";
    private string Scopes => cfg["OIDC:Scopes"] ?? "openid profile email groups";

    public bool Enabled
        => !string.IsNullOrWhiteSpace(Authority)
           && !string.IsNullOrWhiteSpace(ClientId)
           && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>Builds the IdP authorize URL. Returns null when OIDC is disabled.</summary>
    public async Task<string?> CreateSignInRedirectAsync(Uri requestBase, string redirectAfter)
    {
        if (!Enabled)
            return null;

        var d = await GetDiscoveryAsync();

        var state = RandomUrlToken(32);
        var nonce = RandomUrlToken(24);
        var verifier = RandomUrlToken(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var callback = new Uri(requestBase, "/signin/uts").ToString();

        _pending[state] = new Pending(nonce, verifier, callback, redirectAfter, DateTimeOffset.UtcNow);
        PruneStale();

        return $"{d.Authorization}?client_id={Uri.EscapeDataString(ClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(callback)}" +
               "&response_type=code" +
               $"&scope={Uri.EscapeDataString(Scopes)}" +
               $"&state={Uri.EscapeDataString(state)}" +
               $"&nonce={Uri.EscapeDataString(nonce)}" +
               $"&code_challenge={challenge}" +
               "&code_challenge_method=S256";
    }

    /// <summary>Exchanges the code for tokens and builds the authenticated principal.</summary>
    public async Task<ClaimsPrincipal?> CompleteSignInAsync(string? code, string? state, Uri requestBase)
    {
        if (!Enabled)
            return null;

        if (string.IsNullOrWhiteSpace(state) ||
            !_pending.TryRemove(state, out var pending) ||
            pending.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-10) ||
            string.IsNullOrWhiteSpace(code))
            return null;

        var d = await GetDiscoveryAsync();

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["redirect_uri"] = new Uri(requestBase, "/signin/uts").ToString(),
            ["code_verifier"] = pending.CodeVerifier,
        };

        using var res = await http.PostAsync(d.Token, new FormUrlEncodedContent(form));
        if (!res.IsSuccessStatusCode)
        {
            log.LogWarning("OIDC token endpoint returned {Status}", (int)res.StatusCode);
            return null;
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var accessToken = root.TryGetString("access_token");
        var idToken = root.TryGetString("id_token");

        var claims = new Dictionary<string, List<string>>();
        var nonceOk = true;

        if (idToken is not null)
        {
            var payload = DecodeJwtPayload(idToken);
            if (payload is not null)
            {
                if (payload.TryGetString("nonce") is { } n && n != pending.Nonce)
                    nonceOk = false;
                CollectClaims(claims, payload);
            }
        }

        if (!nonceOk)
        {
            log.LogWarning("OIDC nonce mismatch — ignoring sign-in.");
            return null;
        }

        if (accessToken is not null)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, d.Userinfo);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                using var ui = await http.SendAsync(req);
                if (ui.IsSuccessStatusCode)
                {
                    using var ujd = JsonDocument.Parse(await ui.Content.ReadAsStringAsync());
                    CollectClaims(claims, ujd.RootElement);
                }
            }
            catch (Exception e)
            {
                log.LogWarning(e, "OIDC userinfo fetch failed; continuing with id_token claims.");
            }
        }

        var sub = claims.GetValueOrDefault("sub")?.FirstOrDefault();
        if (sub is null)
        {
            log.LogWarning("OIDC sign-in without 'sub' claim.");
            return null;
        }

        var identity = new ClaimsIdentity(
            claims.SelectMany(kv => kv.Value.Select(v => new Claim(kv.Key, v))),
            "oidc",
            "preferred_username",
            "groups");

        return new ClaimsPrincipal(identity);
    }

    private async ValueTask<Discovery> GetDiscoveryAsync()
    {
        if (_discovery is { } cached)
            return cached;

        await _discoveryLock.WaitAsync();
        try
        {
            if (_discovery is { } again)
                return again;

            using var res = await http.GetAsync($"{Authority}/.well-known/openid-configuration");
            res.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            var d = new Discovery(
                r.GetProperty("authorization_endpoint").GetString()!,
                r.GetProperty("token_endpoint").GetString()!,
                r.GetProperty("userinfo_endpoint").GetString()!);
            _discovery = d;
            return d;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    private static void CollectClaims(Dictionary<string, List<string>> target, JsonElement? elOpt)
    {
        if (elOpt is not { } el || el.ValueKind != JsonValueKind.Object)
            return;
        foreach (var prop in el.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in prop.Value.EnumerateArray())
                        if (item.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
                            Add(target, prop.Name, item.ToString());
                    break;
                case JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                    Add(target, prop.Name, prop.Value.ToString());
                    break;
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> target, string key, string value)
    {
        if (!target.TryGetValue(key, out var list))
            target[key] = list = [];
        if (!list.Contains(value))
            list.Add(value);
    }

    private static JsonElement? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
            return null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(ToRegularBase64(parts[1])));
            return JsonDocument.Parse(json).RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string ToRegularBase64(string s)
        => s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4);

    private static string RandomUrlToken(int bytes)
        => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void PruneStale()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-10);
        foreach (var (key, value) in _pending)
            if (value.CreatedAt < cutoff)
                _pending.TryRemove(key, out _);
    }
}

file static class JsonExtensions
{
    public static string? TryGetString(this JsonElement e, string name)
        => ((JsonElement?)e).TryGetString(name);

    public static string? TryGetString(this JsonElement? el, string name)
        => el is { } e && e.ValueKind == JsonValueKind.Object &&
           e.TryGetProperty(name, out var v) &&
           v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
