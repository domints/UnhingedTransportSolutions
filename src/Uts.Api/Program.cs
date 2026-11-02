using System.Security.Claims;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Threading.RateLimiting;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerUI;
using Uts.Api.Db;
using Uts.Api.Endpoints;
using Uts.Api.Http;
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
builder.Services.AddSingleton<AdminIdentity>();
builder.Services.AddSingleton<NumberService>();
builder.Services.AddSingleton<PinService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OidcService>();

// Telegram module (inert without TELEGRAM__BOT_TOKEN)
builder.Services.Configure<TelegramOptions>(cfg.GetSection(TelegramOptions.SectionName));
// Background services crash → keep the app alive (default would stop the
// whole host). The bot is an optional module; everything else has no
// hosted services, so this only ever protects against a bot blowup.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
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
    options.OnRejected = async (context, token) =>
    {
        // Every rejected request gets a Retry-After so scripts can back off politely.
        context.HttpContext.Response.Headers.RetryAfter = "60";
        // Someone is hammering the unlock endpoint — surface it to the courier's Telegram.
        if (context.HttpContext.Request.RouteValues["number"] is not null)
        {
            var notifier = context.HttpContext.RequestServices.GetRequiredService<TelegramNotifier>();
            try
            {
                await notifier.EnqueueAdminAsync($"🚨 Too many wrong access-code attempts for {context.HttpContext.Request.RouteValues["number"]}. Possible brute-force.");
            }
            catch { /* alerts must never break the response */ }
        }
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
    // Global per-IP limit, generous enough that real traffic never sees a 429.
    // A presented-but-unresolved API key (Items set by ApiKeyMiddleware) lands
    // in a much tighter partition that throttles token guessing.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return httpContext.Items.ContainsKey(ApiKeyMiddleware.ItemKeyAuthFailed)
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{ip}|key-guess",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                })
            : RateLimitPartition.GetSlidingWindowLimiter(
                ip,
                _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = 120,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = 6,
                });
    });
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

// OpenAPI document (/openapi/v1.json) + Swagger UI (/swagger).
// On in Development; elsewhere only via `OpenApi:Enabled=true` (OPENAPI__ENABLED).
var openApiEnabled = builder.Environment.IsDevelopment() || cfg.GetValue<bool>("OpenApi:Enabled");
if (openApiEnabled)
{
    builder.Services.AddOpenApi(options =>
    {
        // The document is the integration contract — it must tell script authors
        // that admin routes accept an API key (the route list + per-route
        // permission comes from the operation transformer below).
        options.AddDocumentTransformer((doc, context, ct) =>
        {
            doc.Components ??= new OpenApiComponents();
            doc.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            doc.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "uts_sk",
                Description = "API key minted in the admin panel (uts_sk_…). " +
                              "Key management and identity endpoints accept cookie sessions only.",
            };
            return Task.CompletedTask;
        });
        options.AddOperationTransformer((operation, context, ct) =>
        {
            var desc = context.Description;
            if ((!desc.RelativePath?.StartsWith("api/admin/", StringComparison.Ordinal)) ?? true)
                return Task.CompletedTask;

            var metadata = desc.ActionDescriptor.EndpointMetadata;
            var perm = metadata.OfType<AdminPermAttribute>().FirstOrDefault()?.Perm;
            var keyable = perm is not null
                          && !metadata.OfType<AdminKeysOnlyAttribute>().Any();

            if (keyable)
            {
                // "admin session OR key with <perm>" — one requirement, one scheme.
                operation.Security ??= [];
                operation.Security.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference("ApiKey", context.Document)] = new List<string>(),
                });
            }
            else
            {
                operation.Description = (operation.Description is { } d ? d + " " : "")
                    + "Authentication: admin cookie session only — API keys are not accepted.";
            }

            if (perm is not null)
            {
                operation.Description = (operation.Description is { } d ? d + " " : "")
                    + $"API keys require the '{perm}' permission.";
                operation.Extensions?.Add("x-permission", new JsonNodeExtension(perm));
            }

            return Task.CompletedTask;
        });
    });
}

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

// Swagger UI at /swagger, rendering the built-in OpenAPI document.
if (openApiEnabled)
{
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "UTS API v1");
        options.DocumentTitle = "UTS API";
    });
}

app.UseAuthentication();

// Bearer uts_sk_… API keys (cookie session always wins — see middleware).
app.UseMiddleware<ApiKeyMiddleware>();

app.UseRateLimiter();

// Local-only fake login so the app is testable without an identity provider.
if (cfg.GetValue<bool>("Auth:DevAuth"))
{
    var devAdminGroup = cfg["Auth:AdminGroup"] ?? "uts-admins";
    var devGroupsClaim = cfg["OIDC:GroupsClaim"] ?? "groups";
    // Dev routine: sub is derived from the dev name so the onboarding claim
    // flow works locally too (each distinct name = a distinct identity).
    app.MapPost("/api/dev/login", async (HttpContext http, string? name) =>
    {
        var who = string.IsNullOrWhiteSpace(name) ? "dev-admin" : name.Trim();
        var claims = new List<Claim>
        {
            new("preferred_username", who),
            new("sub", "dev-" + who),
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
            new("sub", "dev-" + who),
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
app.MapAdminIdentity();
app.MapApiKeys();

if (openApiEnabled)
{
    app.MapOpenApi(); // /openapi/v1.json
}

app.MapFallback("/api/{**path}", () => Results.Json(new { error = "not_found" },
    statusCode: StatusCodes.Status404NotFound));
app.MapFallbackToFile("index.html");

app.Run();
