using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Nexiffy.Data;
using Nexiffy.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _config;
        private readonly IPasswordHasher<string> _hasher;
        private readonly ILogger<AuthController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _context;
        private readonly string _jwtSecret;

        // Lock the account for 15 minutes after 10 consecutive failures
        private const int MaxFailedAttempts = 10;
        private const int LockoutMinutes    = 15;
        private const string FailKey  = "Auth:FailedAttempts";
        private const string LockKey  = "Auth:LockedUntil";

        public AuthController(
            IConfiguration config,
            IPasswordHasher<string> hasher,
            ILogger<AuthController> logger,
            IWebHostEnvironment env,
            AppDbContext context,
            [FromKeyedServices("JwtSecret")] string jwtSecret)
        {
            _config     = config;
            _hasher     = hasher;
            _logger     = logger;
            _env        = env;
            _context    = context;
            _jwtSecret  = jwtSecret;
        }

        // ── Helpers ──────────────────────────────────────────────

        private async Task<string?> GetStoredHashAsync()
        {
            var dbHash = (await _context.AppSettings.FindAsync("Auth:PasswordHash"))?.Value;
            if (!string.IsNullOrWhiteSpace(dbHash)) return dbHash;
            var cfgHash = _config["Auth:PasswordHash"];
            return !string.IsNullOrWhiteSpace(cfgHash) ? cfgHash : null;
        }

        private async Task<(bool locked, DateTime until)> CheckLockoutAsync()
        {
            var setting = await _context.AppSettings.FindAsync(LockKey);
            if (setting == null) return (false, default);
            if (DateTime.TryParse(setting.Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                && DateTime.UtcNow < until)
                return (true, until);
            return (false, default);
        }

        private async Task RecordFailedAttemptAsync()
        {
            var fail = await _context.AppSettings.FindAsync(FailKey);
            var count = fail != null && int.TryParse(fail.Value, out var c) ? c + 1 : 1;

            if (fail == null)
                _context.AppSettings.Add(new AppSetting { Key = FailKey, Value = count.ToString(), UpdatedAt = DateTime.UtcNow });
            else
            { fail.Value = count.ToString(); fail.UpdatedAt = DateTime.UtcNow; }

            if (count >= MaxFailedAttempts)
            {
                var until = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                var lockSetting = await _context.AppSettings.FindAsync(LockKey);
                if (lockSetting == null)
                    _context.AppSettings.Add(new AppSetting { Key = LockKey, Value = until.ToString("O"), UpdatedAt = DateTime.UtcNow });
                else
                { lockSetting.Value = until.ToString("O"); lockSetting.UpdatedAt = DateTime.UtcNow; }

                _logger.LogWarning("Account locked until {Until} after {Count} failed attempts from {IP}",
                    until, count, HttpContext.Connection.RemoteIpAddress);
            }
            await _context.SaveChangesAsync();
        }

        private async Task ResetLockoutAsync()
        {
            var fail = await _context.AppSettings.FindAsync(FailKey);
            var lockSetting = await _context.AppSettings.FindAsync(LockKey);
            if (fail != null) _context.AppSettings.Remove(fail);
            if (lockSetting != null) _context.AppSettings.Remove(lockSetting);
            if (fail != null || lockSetting != null)
                await _context.SaveChangesAsync();
        }

        // ── Endpoints ─────────────────────────────────────────────

        [HttpPost("login")]
        [AllowAnonymous]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Username and password required" });

            // Account lockout check
            var (locked, until) = await CheckLockoutAsync();
            if (locked)
            {
                var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
                _logger.LogWarning("Login blocked — account locked for {Min} more minute(s) (IP {IP})",
                    remaining, HttpContext.Connection.RemoteIpAddress);
                return StatusCode(429, new { message = $"Account locked. Try again in {remaining} minute(s)." });
            }

            var validUser = _config["Auth:Username"];
            if (request.Username != validUser)
            {
                await RecordFailedAttemptAsync();
                _logger.LogWarning("Failed login for unknown user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            var storedHash = await GetStoredHashAsync();
            if (storedHash == null)
            {
                _logger.LogWarning("Login attempt with no password hash configured (IP: {IP})", HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            var verifyResult = _hasher.VerifyHashedPassword("", storedHash, request.Password);
            bool passwordValid = verifyResult != PasswordVerificationResult.Failed;

            if (!passwordValid)
            {
                await RecordFailedAttemptAsync();
                _logger.LogWarning("Failed login for user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            // Successful login — reset lockout counter
            await ResetLockoutAsync();

            // Issue JWT with a unique JTI so individual tokens can be revoked at logout
            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "nexiffy-pharmacy",
                audience: "nexiffy-pos",
                claims: new[]
                {
                    new Claim(ClaimTypes.Name, request.Username),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
                },
                expires: DateTime.UtcNow.AddHours(2),
                signingCredentials: creds);

            Response.Cookies.Append("nexiffy_auth", new JwtSecurityTokenHandler().WriteToken(token),
                new CookieOptions
                {
                    HttpOnly  = true,
                    SameSite  = SameSiteMode.Strict,
                    Secure    = !_env.IsDevelopment(),
                    MaxAge    = TimeSpan.FromHours(2),
                    Path      = "/"
                });

            _logger.LogInformation("User '{User}' logged in from {IP}",
                request.Username, HttpContext.Connection.RemoteIpAddress);

            return Ok(new { username = request.Username });
        }

        [HttpPost("logout")]
        [AllowAnonymous]
        public async Task<IActionResult> Logout()
        {
            // Blacklist the JWT so it can't be replayed before it naturally expires
            var raw = Request.Cookies["nexiffy_auth"];
            if (raw != null)
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    if (handler.CanReadToken(raw))
                    {
                        var jwt = handler.ReadJwtToken(raw);
                        var jti = jwt.Id;
                        if (!string.IsNullOrEmpty(jti) && jwt.ValidTo > DateTime.UtcNow)
                        {
                            if (!await _context.RevokedTokens.AnyAsync(t => t.Jti == jti))
                                _context.RevokedTokens.Add(new RevokedToken { Jti = jti, ExpiresAt = jwt.ValidTo });
                            await _context.SaveChangesAsync();
                        }
                    }
                }
                catch (Exception ex) { _logger.LogDebug(ex, "Malformed token on logout — clearing cookie anyway"); }
            }
            Response.Cookies.Delete("nexiffy_auth", new CookieOptions { Path = "/" });
            return Ok();
        }

        [HttpGet("me")]
        [Authorize]
        public IActionResult Me() => Ok(new { username = User.Identity?.Name });

        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CurrentPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
                return BadRequest(new { message = "All fields required" });

            if (req.NewPassword.Length < 8)
                return BadRequest(new { message = "New password must be at least 8 characters" });

            var storedHash = await GetStoredHashAsync();

            if (storedHash == null)
                return BadRequest(new { message = "No password hash configured. Set Auth:PasswordHash in appsettings.json first." });

            var cpResult = _hasher.VerifyHashedPassword("", storedHash, req.CurrentPassword);
            bool currentValid = cpResult != PasswordVerificationResult.Failed;

            if (!currentValid)
                return BadRequest(new { message = "Current password is incorrect" });

            var newHash = _hasher.HashPassword("", req.NewPassword);
            var setting = await _context.AppSettings.FindAsync("Auth:PasswordHash");
            if (setting == null)
                _context.AppSettings.Add(new AppSetting { Key = "Auth:PasswordHash", Value = newHash, UpdatedAt = DateTime.UtcNow });
            else
            { setting.Value = newHash; setting.UpdatedAt = DateTime.UtcNow; }

            await _context.SaveChangesAsync();
            _logger.LogInformation("Password changed by {User}", User.Identity?.Name);
            return Ok(new { message = "Password changed successfully" });
        }
    }
}
