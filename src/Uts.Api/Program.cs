using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Uts.Api.Db;
using Uts.Api.Endpoints;
using Uts.Api.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Content root = the app's own directory, not the caller's CWD — otherwise
    // `dotnet path/to/Uts.Api.dll` from elsewhere serves 404 for static files.
    ContentRootPath = AppContext.BaseDirectory,
});
// Postgres text[] → string[] (Dapper refuses arrays without a handler).
SqlMapper.AddTypeHandler(new StringArrayTypeHandler());
var cfg = builder.Configuration;

builder.Services.AddSingleton<Database>();
builder.Services.AddSingleton<NumberService>();
builder.Services.AddSingleton<PinService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OidcService>();

// Telegram module (inert without TELEGRAM__BOT_TOKEN)
builder.Services.Configure<TelegramOptions>(cfg.GetSection(TelegramOptions.SectionName));
// Consumers may inject the raw options type — make it resolvable
// (Configure<> alone registers only IOptions<>).
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IOptionsMonitor<TelegramOptions>>().CurrentValue);
builder.Services.AddSingleton<BotState>();
builder.Services.AddSingleton<TelegramNotifier>();
builder.Services.AddSingleton<TripBulk>();
builder.Services.AddHostedService<TelegramBot>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Someone is hammering the unlock endpoint — surface it to the courier's Telegram.
    options.OnRejected = async (context, token) =>
    {
        var number = context.HttpContext.Request.RouteValues["number"]?.ToString() ?? "a tracking number";
        var notifier = context.HttpContext.RequestServices.GetRequiredService<TelegramNotifier>();
        try
        {
            await notifier.EnqueueAdminAsync($"🚨 Too many wrong access-code attempts for {number}. Possible brute-force.");
        }
        catch { /* alerts must never break the response */ }
        await Task.CompletedTask;
    };
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

Console.WriteLine($"[{DateTime.Now:T}] checkpoints: built");

await DbMigrator.RunAsync(app.Services);

Console.WriteLine($"[{DateTime.Now:T}] checkpoints: migrated");

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

    // Browser-friendly dev login (GET with side effect is fine — this endpoint
    // only exists when DevAuth is explicitly enabled).
    app.MapGet("/dev/login", async (HttpContext http, string? name) =>
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
        return Results.Redirect("/admin");
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
