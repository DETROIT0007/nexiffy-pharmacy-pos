using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Nexffy.Data;
using Nexffy.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Nexffy.Controllers
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

        public AuthController(
            IConfiguration config,
            IPasswordHasher<string> hasher,
            ILogger<AuthController> logger,
            IWebHostEnvironment env,
            AppDbContext context)
        {
            _config = config;
            _hasher = hasher;
            _logger = logger;
            _env = env;
            _context = context;
        }

        // Resolve the active password hash: DB override > config hash > plaintext fallback
        private async Task<string?> GetStoredHashAsync()
        {
            var dbHash = (await _context.AppSettings.FindAsync("Auth:PasswordHash"))?.Value;
            if (!string.IsNullOrWhiteSpace(dbHash)) return dbHash;
            var cfgHash = _config["Auth:PasswordHash"];
            return !string.IsNullOrWhiteSpace(cfgHash) ? cfgHash : null;
        }

        [HttpPost("login")]
        [AllowAnonymous]
        [EnableRateLimiting("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Username and password required" });

            var validUser = _config["Auth:Username"];
            if (request.Username != validUser)
            {
                _logger.LogWarning("Failed login for unknown user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            bool passwordValid;
            var storedHash = await GetStoredHashAsync();

            if (storedHash != null)
            {
                var result = _hasher.VerifyHashedPassword("", storedHash, request.Password);
                passwordValid = result != PasswordVerificationResult.Failed;
            }
            else
            {
                // Plaintext fallback — migration path; startup logs the hash once
                passwordValid = request.Password == _config["Auth:Password"];
            }

            if (!passwordValid)
            {
                _logger.LogWarning("Failed login for user '{User}' from {IP}",
                    request.Username, HttpContext.Connection.RemoteIpAddress);
                return Unauthorized(new { message = "Invalid credentials" });
            }

            var secret = _config["Auth:JwtSecret"]!;
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "nexffy-pharmacy",
                audience: "nexffy-pos",
                claims: new[] { new Claim(ClaimTypes.Name, request.Username) },
                expires: DateTime.UtcNow.AddHours(2),
                signingCredentials: creds);

            Response.Cookies.Append("nexffy_auth", new JwtSecurityTokenHandler().WriteToken(token),
                new CookieOptions
                {
                    HttpOnly = true,
                    SameSite = SameSiteMode.Strict,
                    Secure = !_env.IsDevelopment(),
                    MaxAge = TimeSpan.FromHours(2),
                    Path = "/"
                });

            _logger.LogInformation("User '{User}' logged in from {IP}",
                request.Username, HttpContext.Connection.RemoteIpAddress);

            return Ok(new { username = request.Username });
        }

        [HttpPost("logout")]
        [AllowAnonymous]
        public IActionResult Logout()
        {
            Response.Cookies.Delete("nexffy_auth", new CookieOptions { Path = "/" });
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

            bool currentValid;
            if (storedHash != null)
            {
                var result = _hasher.VerifyHashedPassword("", storedHash, req.CurrentPassword);
                currentValid = result != PasswordVerificationResult.Failed;
            }
            else
            {
                currentValid = req.CurrentPassword == _config["Auth:Password"];
            }

            if (!currentValid)
                return BadRequest(new { message = "Current password is incorrect" });

            var newHash = _hasher.HashPassword("", req.NewPassword);
            var setting = await _context.AppSettings.FindAsync("Auth:PasswordHash");
            if (setting == null)
                _context.AppSettings.Add(new AppSetting { Key = "Auth:PasswordHash", Value = newHash, UpdatedAt = DateTime.Now });
            else
            {
                setting.Value = newHash;
                setting.UpdatedAt = DateTime.Now;
            }
            await _context.SaveChangesAsync();

            _logger.LogInformation("Password changed by {User}", User.Identity?.Name);
            return Ok(new { message = "Password changed successfully" });
        }
    }
}
