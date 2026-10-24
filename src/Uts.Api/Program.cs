using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Uts.Api.Db;
using Uts.Api.Endpoints;
using Uts.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<NumberService>();
builder.Services.AddSingleton<PinService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OidcService>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("pin-unlock", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}|{httpContext.Request.RouteValues["number"]}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(10),
            }));
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "uts.session";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

var app = builder.Build();

await DbMigrator.RunAsync(app.Services);

if (cfg.GetValue<bool>("Auth:BehindProxy"))
{
    // Plain HTTP internally: TLS is terminated by the reverse proxy, which sets
    // X-Forwarded-For/Proto. Trust forwarded headers from any peer — safe as long
    // as this port is not exposed directly to untrusted networks (dispatch: only
    // the reverse proxy should reach it; keep UTS_PORT unpublished in prod).
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        KnownNetworks = { },
        KnownProxies = { },
    });
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseRateLimiter();

// Local-only fake login so the app is testable without an identity provider.
if (cfg.GetValue<bool>("Auth:DevAuth"))
{
    var devAdminGroup = cfg["Auth:AdminGroup"] ?? "uts-admins";
    var devGroupsClaim = cfg["OIDC:GroupsClaim"] ?? "groups";
    app.MapPost("/api/dev/login", async (HttpContext http, string? name) =>
    {
        var who = string.IsNullOrWhiteSpace(name) ? "dev-admin" : name.Trim();
        var claims = new List<Claim>
        {
            new("preferred_username", who),
            new(devGroupsClaim, devAdminGroup),
        };
        var identity = new ClaimsIdentity(claims, "dev");
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));
        return Results.Ok(new { ok = true, name = who });
    });
}

app.MapAuthEndpoints();
app.MapTrackEndpoints();

app.MapAdminShipments();
app.MapAdminCatalog();

app.MapFallback("/api/{**path}", () => Results.Json(new { error = "not_found" },
    statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();
