using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexiffy.Data;
using Nexiffy.Models;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CategoriesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<CategoriesController> _logger;

        public CategoriesController(AppDbContext context, ILogger<CategoriesController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var medCounts = await _context.Medicines
                .Where(m => m.Category != "")
                .GroupBy(m => m.Category)
                .Select(g => new { name = g.Key, count = g.Count() })
                .ToListAsync();

            var dbCats = await _context.Categories.Select(c => c.Name).ToListAsync();

            var result = medCounts.ToDictionary(x => x.name, x => x.count);
            foreach (var cat in dbCats)
            {
                if (!result.ContainsKey(cat))
                    result[cat] = 0;
            }

            return Ok(result
                .OrderBy(kv => kv.Key)
                .Select(kv => new { name = kv.Key, medicineCount = kv.Value }));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CategoryCreateRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return BadRequest(new { message = "Category name is required" });

            var name = req.Name.Trim();
            if (name.Length > 100)
                return BadRequest(new { message = "Category name too long (max 100 characters)" });

            if (await _context.Categories.AnyAsync(c => c.Name == name))
                return Conflict(new { message = "Category already exists" });

            _context.Categories.Add(new Category { Name = name });
            await _context.SaveChangesAsync();

            _logger.LogInformation("Category '{Name}' added by {User}", name, User.Identity?.Name);
            return Ok(new { name });
        }

        [HttpDelete("{name}")]
        public async Task<IActionResult> Delete(string name)
        {
            if (await _context.Medicines.AnyAsync(m => m.Category == name))
                return BadRequest(new { message = "Cannot remove: medicines are assigned to this category" });

            var cat = await _context.Categories.FindAsync(name);
            if (cat == null) return NotFound(new { message = "Category not found" });

            _context.Categories.Remove(cat);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Category '{Name}' removed by {User}", name, User.Identity?.Name);
            return Ok(new { message = "Category removed" });
        }
    }

    public record CategoryCreateRequest(string Name);
}
