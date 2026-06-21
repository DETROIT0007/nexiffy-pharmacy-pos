using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nexffy.Data;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Services ────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IPasswordHasher<string>, PasswordHasher<string>>();

// ── JWT Auth ─────────────────────────────────────────
var jwtSecret = builder.Configuration["Auth:JwtSecret"]!;
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
        // Read JWT from HttpOnly cookie instead of Authorization header
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var cookie = context.Request.Cookies["nexffy_auth"];
                if (!string.IsNullOrEmpty(cookie))
                    context.Token = cookie;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// ── CORS ─────────────────────────────────────────────
var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
    ?? new[] { "http://localhost:5200", "http://127.0.0.1:5200" };
builder.Services.AddCors(options =>
    options.AddPolicy("LocalOnly", p =>
        p.WithOrigins(corsOrigins)
         .AllowAnyMethod()
         .AllowAnyHeader()
         .AllowCredentials()));

// ── Rate Limiting ────────────────────────────────────
builder.Services.AddRateLimiter(opts =>
{
    // General API: 60 req/min
    opts.AddFixedWindowLimiter("api", o =>
    {
        o.PermitLimit = 60;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    // Login: 5 attempts/min per client — strict brute-force guard
    opts.AddFixedWindowLimiter("login", o =>
    {
        o.PermitLimit = 5;
        o.Window = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit = 0;
    });
    opts.RejectionStatusCode = 429;
});

// ── Database ─────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// ── Security Headers ─────────────────────────────────
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Frame-Options"]           = "DENY";
    h["X-Content-Type-Options"]    = "nosniff";
    h["X-XSS-Protection"]          = "1; mode=block";
    h["Referrer-Policy"]           = "strict-origin-when-cross-origin";
    // unsafe-inline needed because the SPA uses inline <script> and <style>
    h["Content-Security-Policy"]   =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'";
    await next();
});

// ── Middleware ───────────────────────────────────────
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

// ── Database Setup ───────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        if (app.Environment.IsDevelopment())
        {
            context.Database.EnsureDeleted();
            Console.WriteLine("Dev: DB dropped for schema refresh.");
        }
        context.Database.EnsureCreated();
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
    Console.WriteLine($"   Add this to appsettings.json under \"Auth\": {{ \"PasswordHash\": \"{hash}\" }}");
    Console.WriteLine("   Then remove the \"Password\" key.");
}

var port = builder.Configuration["Server:Port"] ?? "5200";
app.Run($"http://0.0.0.0:{port}");
