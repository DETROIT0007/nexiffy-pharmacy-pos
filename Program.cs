using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nexffy.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

// ── JWT Secret — stored in OS data directory, never in source ───────
// On first run a cryptographically random key is generated and written to
// %PROGRAMDATA%\Nexffy\jwt.key so it persists across restarts without
// ever appearing in appsettings.json or source control.
static string GetOrCreateJwtSecret()
{
    var keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexffy", "jwt.key");

    if (File.Exists(keyPath))
    {
        var stored = File.ReadAllText(keyPath).Trim();
        if (stored.Length >= 32) return stored;
    }

    var bytes = new byte[64];
    RandomNumberGenerator.Fill(bytes);
    var secret = Convert.ToBase64String(bytes);
    Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
    File.WriteAllText(keyPath, secret);
    Console.WriteLine($"[Nexffy] JWT secret generated → {keyPath}");
    return secret;
}

var jwtSecret = GetOrCreateJwtSecret();

var builder = WebApplication.CreateBuilder(args);

// ── Services ─────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();

// ── JWT Auth ──────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateIssuer = true,
            ValidIssuer = "nexffy-pharmacy",
            ValidateAudience = true,
            ValidAudience = "nexffy-pos",
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var cookie = ctx.Request.Cookies["nexffy_auth"];
                if (!string.IsNullOrEmpty(cookie)) ctx.Token = cookie;
                return Task.CompletedTask;
            },
            // Reject any token whose JTI was revoked at logout
            OnTokenValidated = async ctx =>
            {
                var jti = ctx.Principal?.FindFirstValue("jti");
                if (string.IsNullOrEmpty(jti)) return;
                var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                if (await db.RevokedTokens.AnyAsync(t => t.Jti == jti))
                    ctx.Fail("Token has been revoked");
            }
        };
    });

builder.Services.AddAuthorization();

// ── CORS ──────────────────────────────────────────────
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:5200", "http://127.0.0.1:5200" };
builder.Services.AddCors(options =>
    options.AddPolicy("LocalOnly", p =>
        p.WithOrigins(corsOrigins)
         .AllowAnyMethod()
         .AllowAnyHeader()
         .AllowCredentials()));

// ── Rate Limiting ─────────────────────────────────────
builder.Services.AddRateLimiter(opts =>
{
    opts.AddFixedWindowLimiter("api", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    opts.AddFixedWindowLimiter("login", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    opts.RejectionStatusCode = 429;
});

// ── Database ──────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// ── Security Headers ──────────────────────────────────
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Frame-Options"]         = "DENY";
    h["X-Content-Type-Options"]  = "nosniff";
    h["X-XSS-Protection"]        = "1; mode=block";
    h["Referrer-Policy"]         = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    await next();
});

// ── Middleware ────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("LocalOnly");
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers().RequireRateLimiting("api");

// ── Database Setup ────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        // DB wipe requires an explicit opt-in flag — being in Development mode alone is not enough.
        // To enable: set "Dev:ResetDbOnStart": true in appsettings.json (or env NEXFFY_DEV_RESET_DB=true)
        if (app.Environment.IsDevelopment() &&
            app.Configuration.GetValue<bool>("Dev:ResetDbOnStart"))
        {
            context.Database.EnsureDeleted();
            Console.WriteLine("[Nexffy] Dev: DB wiped per Dev:ResetDbOnStart flag.");
        }
        context.Database.EnsureCreated();

        // Prune expired revoked tokens on startup
        var pruned = await context.RevokedTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync();
        if (pruned > 0) Console.WriteLine($"[Nexffy] Pruned {pruned} expired revoked token(s).");

        Console.WriteLine("NexffyDB ready.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"DB error: {ex.Message}");
    }
}

// One-time startup warning if password is still stored as plaintext
if (string.IsNullOrWhiteSpace(app.Configuration["Auth:PasswordHash"]) &&
    !string.IsNullOrWhiteSpace(app.Configuration["Auth:Password"]))
{
    var hasher = app.Services.GetRequiredService<IPasswordHasher<string>>();
    var hash = hasher.HashPassword("", app.Configuration["Auth:Password"]!);
    Console.WriteLine("⚠  SECURITY: plaintext password detected in configuration.");
    Console.WriteLine($"   Set Auth:PasswordHash = \"{hash}\" in appsettings.json and remove Auth:Password.");
}

var port = builder.Configuration["Server:Port"] ?? "5200";
app.Run($"http://0.0.0.0:{port}");
