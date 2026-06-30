using System.Threading.RateLimiting;
using DM.LicenseServer.Data;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

// ── Cloud hosting: write RSA key files from environment variables ─────────────
// Agar keys/ folder nahi hai (Railway, Render, Docker) to env vars se files
// create ho jaate hain. Set SIGNING_KEY_CONTENT and SIGNING_PUB_CONTENT.
// Value mein literal \n type karo (Railway env vars support multi-line too).
{
    var privContent = Environment.GetEnvironmentVariable("SIGNING_KEY_CONTENT");
    var pubContent  = Environment.GetEnvironmentVariable("SIGNING_PUB_CONTENT");
    if (privContent is not null || pubContent is not null)
    {
        var keysDir = Path.Combine(Directory.GetCurrentDirectory(), "keys");
        Directory.CreateDirectory(keysDir);
        if (privContent is not null)
            File.WriteAllText(Path.Combine(keysDir, "signing.key"),
                privContent.Replace("\\n", "\n"));
        if (pubContent is not null)
            File.WriteAllText(Path.Combine(keysDir, "signing.pub"),
                pubContent.Replace("\\n", "\n"));
    }
}

var builder = WebApplication.CreateBuilder(args);
var cfg     = builder.Configuration;

// ── Database ──────────────────────────────────────────────────────────────────
// Switch to PostgreSQL: set Database:Provider = "postgres" and fill
// Database:PostgresConnectionString in appsettings.json (or env var).

var dbProvider = cfg["Database:Provider"] ?? "sqlite";

builder.Services.AddDbContext<LicenseDbContext>(opts =>
{
    if (dbProvider.Equals("postgres", StringComparison.OrdinalIgnoreCase))
        opts.UseNpgsql(cfg["Database:PostgresConnectionString"]
            ?? throw new InvalidOperationException(
                "Database:PostgresConnectionString must be set when Provider=postgres"));
    else
        opts.UseSqlite(cfg["Database:SqliteConnectionString"] ?? "Data Source=licensedb.sqlite");
});

// ── Signing + business logic ──────────────────────────────────────────────────
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddScoped<IAbuseDetectionService, AbuseDetectionService>();
builder.Services.AddScoped<ILicenseService, LicenseService>();

// ── Admin cookie auth ─────────────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath         = "/login";
        o.ExpireTimeSpan    = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly   = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite   = SameSiteMode.Strict;
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole("admin"));

// ── Rate limiting ─────────────────────────────────────────────────────────────
// Uses the built-in Microsoft.AspNetCore.RateLimiting (no extra package).
// Partitioned by remote IP address; each policy has its own sliding window.

builder.Services.AddRateLimiter(opts =>
{
    // /activate  — 10 activations per minute per IP
    opts.AddPolicy("activate", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit        = 10,
                Window             = TimeSpan.FromMinutes(1),
                AutoReplenishment  = true,
            }));

    // /validate  — 120 heartbeats per minute per IP (e.g. 10 apps × once every 5s)
    opts.AddPolicy("validate", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit        = 120,
                Window             = TimeSpan.FromMinutes(1),
                AutoReplenishment  = true,
            }));

    // /report    — 30 reports per hour per IP (enough for legitimate tamper reports)
    opts.AddPolicy("report", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = 30,
                Window            = TimeSpan.FromHours(1),
                AutoReplenishment = true,
            }));

    // /session-data — 60 per hour per IP (one refresh every minute if needed)
    opts.AddPolicy("session", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit       = 60,
                Window            = TimeSpan.FromHours(1),
                AutoReplenishment = true,
            }));

    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    opts.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers["Retry-After"] = "60";
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limit_exceeded", retryAfterSeconds = 60 }, ct);
    };
});

// ── MVC + Razor Pages ─────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddRazorPages(opts =>
{
    // All /Admin/* pages require the AdminOnly policy
    opts.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
});

builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ── DB init ───────────────────────────────────────────────────────────────────
// EnsureCreated is fine for SQLite dev; for production use proper migrations:
//   dotnet ef migrations add Initial --project DM.LicenseServer
//   dotnet ef database update
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
    db.Database.EnsureCreated();
}

// ── Middleware pipeline ───────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapRazorPages();
app.MapGet("/", () => Results.Redirect("/admin"));

app.Run();
