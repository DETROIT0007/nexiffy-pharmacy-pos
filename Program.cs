using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nexiffy.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

// ── JWT Secret — stored in OS data directory, never in source ───────
// On first run a cryptographically random key is generated and written to
// %PROGRAMDATA%\Nexiffy\jwt.key so it persists across restarts without
// ever appearing in appsettings.json or source control.
static string GetOrCreateJwtSecret()
{
    var keyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Nexiffy", "jwt.key");

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

    // Lock down the key file to the current user only (Windows)
    if (OperatingSystem.IsWindows())
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "icacls",
            Arguments = $"\"{keyPath}\" /inheritance:r /grant:r \"{Environment.UserDomainName}\\{Environment.UserName}:(F)\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        p?.WaitForExit(3000);
    }

    Console.WriteLine($"[Nexiffy] JWT secret generated → {keyPath}");
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
            ValidIssuer = "nexiffy-pharmacy",
            ValidateAudience = true,
            ValidAudience = "nexiffy-pos",
            ClockSkew = TimeSpan.Zero
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var cookie = ctx.Request.Cookies["nexiffy_auth"];
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
            Console.WriteLine("[Nexiffy] Dev: DB wiped per Dev:ResetDbOnStart flag.");
        }
        context.Database.EnsureCreated();

        // Create RevokedTokens table if the DB was created before this table was added
        await context.Database.ExecuteSqlRawAsync(@"
            IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'RevokedTokens')
            BEGIN
                CREATE TABLE [RevokedTokens] (
                    [Jti] NVARCHAR(200) NOT NULL,
                    [ExpiresAt] DATETIME2 NOT NULL,
                    CONSTRAINT [PK_RevokedTokens] PRIMARY KEY ([Jti])
                );
                CREATE INDEX [IX_RevokedTokens_ExpiresAt] ON [RevokedTokens] ([ExpiresAt]);
            END");

        // Seed 10 sample medicines if no active (non-deleted) medicines exist
        if (!context.Medicines.IgnoreQueryFilters().Any(m => !m.IsDeleted))
        {
            var today = DateTime.Now;
            context.Medicines.AddRange(new[]
            {
                new Nexiffy.Models.Medicine { Code="MED-001", Name="Panadol 500mg",       GenericName="Paracetamol",       Category="Painkiller",   Unit="Strip",  Price=35m,   Stock=120, ExpiryDate=today.AddMonths(18).ToString("yyyy-MM-dd"), Manufacturer="GSK",          LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-002", Name="Brufen 400mg",        GenericName="Ibuprofen",         Category="Painkiller",   Unit="Strip",  Price=45m,   Stock=80,  ExpiryDate=today.AddMonths(14).ToString("yyyy-MM-dd"), Manufacturer="Abbott",       LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-003", Name="Augmentin 625mg",     GenericName="Amoxicillin+CA",    Category="Antibiotic",   Unit="Strip",  Price=320m,  Stock=5,   ExpiryDate=today.AddMonths(10).ToString("yyyy-MM-dd"), Manufacturer="GSK",          LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-004", Name="Nexium 40mg",         GenericName="Esomeprazole",      Category="Antacid",      Unit="Strip",  Price=280m,  Stock=42,  ExpiryDate=today.AddMonths(20).ToString("yyyy-MM-dd"), Manufacturer="AstraZeneca",  LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-005", Name="Metformin 500mg",     GenericName="Metformin HCl",     Category="Diabetes",     Unit="Strip",  Price=60m,   Stock=200, ExpiryDate=today.AddMonths(24).ToString("yyyy-MM-dd"), Manufacturer="Getz Pharma",  LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-006", Name="Amlodipine 5mg",      GenericName="Amlodipine",        Category="Cardiac",      Unit="Strip",  Price=55m,   Stock=8,   ExpiryDate=today.AddDays(25).ToString("yyyy-MM-dd"),   Manufacturer="Pfizer",       LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-007", Name="Atorvastatin 20mg",   GenericName="Atorvastatin",      Category="Cardiac",      Unit="Strip",  Price=140m,  Stock=60,  ExpiryDate=today.AddMonths(16).ToString("yyyy-MM-dd"), Manufacturer="Searle",       LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-008", Name="ORS Sachet",          GenericName="Oral Rehydration",  Category="Gastro",       Unit="Sachet", Price=12m,   Stock=3,   ExpiryDate=today.AddMonths(12).ToString("yyyy-MM-dd"), Manufacturer="Otsuka",       LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-009", Name="Ventolin Inhaler",    GenericName="Salbutamol",        Category="Respiratory",  Unit="Bottle", Price=380m,  Stock=15,  ExpiryDate=today.AddMonths(22).ToString("yyyy-MM-dd"), Manufacturer="GSK",          LastUpdated=today },
                new Nexiffy.Models.Medicine { Code="MED-010", Name="Vitamin C 500mg",     GenericName="Ascorbic Acid",     Category="Supplement",   Unit="Strip",  Price=25m,   Stock=150, ExpiryDate=today.AddDays(18).ToString("yyyy-MM-dd"),   Manufacturer="Ferozsons",    LastUpdated=today },
            });
            await context.SaveChangesAsync();
            Console.WriteLine("[Nexiffy] Seeded 10 sample medicines.");
        }

        // Prune expired revoked tokens on startup
        var pruned = await context.RevokedTokens
            .Where(t => t.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync();
        if (pruned > 0) Console.WriteLine($"[Nexiffy] Pruned {pruned} expired revoked token(s).");

        Console.WriteLine("NexiffyDB ready.");
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
