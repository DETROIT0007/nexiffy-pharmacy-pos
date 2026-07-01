using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexiffy.Data;
using Nexiffy.Models;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Roles = UserRole.Admin)]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly IPasswordHasher<string> _hasher;
        private readonly ILogger<UsersController> _logger;

        public UsersController(AppDbContext context, IPasswordHasher<string> hasher, ILogger<UsersController> logger)
        {
            _context = context;
            _hasher = hasher;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var users = await _context.Users
                .OrderBy(u => u.Username)
                .Select(u => new { u.Id, u.Username, u.Role, u.IsActive, u.MustChangePassword, u.CreatedAt, u.CreatedBy })
                .ToListAsync();
            return Ok(users);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateUserRequest req)
        {
            var username = req.Username?.Trim() ?? "";
            if (username.Length < 3 || username.Length > 100)
                return BadRequest(new { message = "Username must be 3-100 characters" });
            if (string.IsNullOrWhiteSpace(req.TemporaryPassword) || req.TemporaryPassword.Length < 8)
                return BadRequest(new { message = "Temporary password must be at least 8 characters" });
            if (req.Role != UserRole.Admin && req.Role != UserRole.Salesman)
                return BadRequest(new { message = "Role must be Admin or Salesman" });

            if (await _context.Users.AnyAsync(u => u.Username == username))
                return Conflict(new { message = "Username already exists" });

            var user = new AppUser
            {
                Username = username,
                PasswordHash = _hasher.HashPassword("", req.TemporaryPassword),
                Role = req.Role,
                MustChangePassword = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = User.Identity?.Name
            };
            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            _logger.LogInformation("User '{Username}' ({Role}) created by {Admin}",
                user.Username, user.Role, User.Identity?.Name);

            return Ok(new { user.Id, user.Username, user.Role, user.IsActive, user.MustChangePassword });
        }

        [HttpPut("{id}/deactivate")]
        public async Task<IActionResult> Deactivate(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();
            if (user.Username == User.Identity?.Name)
                return BadRequest(new { message = "You can't deactivate your own account" });

            user.IsActive = false;
            await _context.SaveChangesAsync();
            _logger.LogInformation("User '{Username}' deactivated by {Admin}", user.Username, User.Identity?.Name);
            return Ok(new { message = "User deactivated" });
        }

        [HttpPut("{id}/reactivate")]
        public async Task<IActionResult> Reactivate(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.IsActive = true;
            await _context.SaveChangesAsync();
            _logger.LogInformation("User '{Username}' reactivated by {Admin}", user.Username, User.Identity?.Name);
            return Ok(new { message = "User reactivated" });
        }

        [HttpPut("{id}/reset-password")]
        public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.NewTemporaryPassword) || req.NewTemporaryPassword.Length < 8)
                return BadRequest(new { message = "Temporary password must be at least 8 characters" });

            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            user.PasswordHash = _hasher.HashPassword("", req.NewTemporaryPassword);
            user.MustChangePassword = true;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Password reset for '{Username}' by {Admin}", user.Username, User.Identity?.Name);
            return Ok(new { message = "Password reset — user must change it on next login" });
        }
    }
}
