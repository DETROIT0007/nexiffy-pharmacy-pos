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
        private readonly IPasswordHasher<string> _hasher;
        private readonly ILogger<AuthController> _logger;
        private readonly IWebHostEnvironment _env;
        private readonly AppDbContext _context;
        private readonly string _jwtSecret;

        // Lock an individual account for 15 minutes after 10 consecutive failures
        private const int MaxFailedAttempts = 10;
        private const int LockoutMinutes    = 15;

        public AuthController(
            IPasswordHasher<string> hasher,
            ILogger<AuthController> logger,
            IWebHostEnvironment env,
            AppDbContext context,
            [FromKeyedServices("JwtSecret")] string jwtSecret)
        {
            _hasher     = hasher;
            _logger     = logger;
            _env        = env;
            _context    = context;
            _jwtSecret  = jwtSecret;
        }

        // ── Helpers ──────────────────────────────────────────────
        // Lockout state is keyed per attempted username, not globally — one
        // account being brute-forced (or a salesman fat-fingering a password)
        // must never lock out every other account.

        private static string FailKey(string username) => $"Auth:FailedAttempts:{username}";
        private static string LockKey(string username) => $"Auth:LockedUntil:{username}";

        private async Task<(bool locked, DateTime until)> CheckLockoutAsync(string username)
        {
            var setting = await _context.AppSettings.FindAsync(LockKey(username));
            if (setting == null) return (false, default);
            if (DateTime.TryParse(setting.Value, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until)
                && DateTime.UtcNow < until)
                return (true, until);
            return (false, default);
        }

        private async Task RecordFailedAttemptAsync(string username)
        {
            var failKey = FailKey(username);
            var fail = await _context.AppSettings.FindAsync(failKey);
            var count = fail != null && int.TryParse(fail.Value, out var c) ? c + 1 : 1;

            if (fail == null)
                _context.AppSettings.Add(new AppSetting { Key = failKey, Value = count.ToString(), UpdatedAt = DateTime.UtcNow });
            else
            { fail.Value = count.ToString(); fail.UpdatedAt = DateTime.UtcNow; }

            if (count >= MaxFailedAttempts)
            {
                var until = DateTime.UtcNow.AddMinutes(LockoutMinutes);
                var lockKey = LockKey(username);
                var lockSetting = await _context.AppSettings.FindAsync(lockKey);
                if (lockSetting == null)
                    _context.AppSettings.Add(new AppSetting { Key = lockKey, Value = until.ToString("O"), UpdatedAt = DateTime.UtcNow });
                else
                { lockSetting.Value = until.ToString("O"); lockSetting.UpdatedAt = DateTime.UtcNow; }

                _logger.LogWarning("Account '{User}' locked until {Until} after {Count} failed attempts from {IP}",
                    username, until, count, HttpContext.Connection.RemoteIpAddress);
            }
            await _context.SaveChangesAsync();
        }

        private async Task ResetLockoutAsync(string username)
        {
            var fail = await _context.AppSettings.FindAsync(FailKey(username));
            var lockSetting = await _context.AppSettings.FindAsync(LockKey(username));
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

            var (locked, until) = await CheckLockoutAsync(request.Username);
            if (locked)
            {
                var remaining = (int)Math.Ceiling((until - DateTime.UtcNow).TotalMinutes);
                _logger.LogWarning("Login blocked — '{User}' locked for {Min} more minute(s) (IP {IP})",
                    request.Username, remaining, HttpContext.Connection.RemoteIpAddress);
                return StatusCode(429, new { message = $"Account locked. Try again in {remaining} minute(s)." });
            }

            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == request.Username && u.IsActive);
            if (user == null)
            {
                await RecordFailedAttemptAsync(request.Username);
                _logger.LogWarning("Failed login for unknown/inactive user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            var verifyResult = _hasher.VerifyHashedPassword("", user.PasswordHash, request.Password);
            bool passwordValid = verifyResult != PasswordVerificationResult.Failed;

            if (!passwordValid)
            {
                await RecordFailedAttemptAsync(request.Username);
                _logger.LogWarning("Failed login for user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            await ResetLockoutAsync(request.Username);

            // Issue JWT with a unique JTI so individual tokens can be revoked at logout
            var key   = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "nexiffy-pharmacy",
                audience: "nexiffy-pos",
                claims: new[]
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.Role, user.Role),
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

            _logger.LogInformation("User '{User}' ({Role}) logged in from {IP}",
                user.Username, user.Role, HttpContext.Connection.RemoteIpAddress);

            return Ok(new { username = user.Username, role = user.Role, mustChangePassword = user.MustChangePassword });
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
        public async Task<IActionResult> Me()
        {
            var username = User.Identity?.Name;
            var mustChangePassword = await _context.Users
                .Where(u => u.Username == username)
                .Select(u => (bool?)u.MustChangePassword)
                .FirstOrDefaultAsync() ?? false;

            return Ok(new
            {
                username,
                role = User.FindFirst(ClaimTypes.Role)?.Value,
                mustChangePassword
            });
        }

        [HttpPost("change-password")]
        [Authorize]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.CurrentPassword) || string.IsNullOrWhiteSpace(req.NewPassword))
                return BadRequest(new { message = "All fields required" });

            if (req.NewPassword.Length < 8)
                return BadRequest(new { message = "New password must be at least 8 characters" });

            var username = User.Identity?.Name;
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user == null)
                return Unauthorized();

            var cpResult = _hasher.VerifyHashedPassword("", user.PasswordHash, req.CurrentPassword);
            if (cpResult == PasswordVerificationResult.Failed)
                return BadRequest(new { message = "Current password is incorrect" });

            user.PasswordHash = _hasher.HashPassword("", req.NewPassword);
            user.MustChangePassword = false;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Password changed by {User}", username);
            return Ok(new { message = "Password changed successfully" });
        }
    }
}
