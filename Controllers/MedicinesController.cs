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
    public class MedicinesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<MedicinesController> _logger;

        public MedicinesController(AppDbContext context, ILogger<MedicinesController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? search,
            [FromQuery] string? category,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (page > 100000) page = 100000;
            if (pageSize < 1 || pageSize > 500) pageSize = 20;

            var query = _context.Medicines.AsQueryable();

            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(m =>
                    m.Name.Contains(search) ||
                    m.GenericName.Contains(search) ||
                    m.Code.Contains(search));

            if (!string.IsNullOrWhiteSpace(category))
                query = query.Where(m => m.Category == category);

            var total = await query.CountAsync();
            var items = await query
                .OrderBy(m => m.Name)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { items, total, page, pageSize });
        }

        [HttpGet("{code}")]
        public async Task<ActionResult<Medicine>> GetOne(string code)
        {
            var med = await _context.Medicines.FirstOrDefaultAsync(m => m.Code == code);
            return med == null ? NotFound() : Ok(med);
        }

        [HttpPost]
        public async Task<ActionResult<Medicine>> Create(Medicine medicine)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (await _context.Medicines.IgnoreQueryFilters().AnyAsync(m => m.Code == medicine.Code))
                return Conflict(new { message = "Medicine code already exists" });

            medicine.LastUpdated = DateTime.UtcNow;
            _context.Medicines.Add(medicine);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Medicine '{Code}' ({Name}) added by {User}",
                medicine.Code, medicine.Name, User.Identity?.Name);

            return Ok(medicine);
        }

        [HttpPut("{code}")]
        public async Task<IActionResult> Update(string code, Medicine medicine)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            var existing = await _context.Medicines.FirstOrDefaultAsync(m => m.Code == code);
            if (existing == null) return NotFound();

            _logger.LogInformation(
                "Medicine '{Code}' updated by {User} — Price: {OldPrice}->{NewPrice}, Stock: {OldStock}->{NewStock}",
                code, User.Identity?.Name, existing.Price, medicine.Price, existing.Stock, medicine.Stock);

            existing.Name         = medicine.Name;
            existing.GenericName  = medicine.GenericName;
            existing.Category     = medicine.Category;
            existing.Unit         = medicine.Unit;
            existing.Price        = medicine.Price;
            existing.Stock        = medicine.Stock;
            existing.ExpiryDate   = medicine.ExpiryDate;
            existing.Manufacturer = medicine.Manufacturer;
            existing.LastUpdated  = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            return Ok(existing);
        }

        [HttpDelete("{code}")]
        public async Task<IActionResult> Delete(string code)
        {
            var med = await _context.Medicines.FirstOrDefaultAsync(m => m.Code == code);
            if (med == null) return NotFound();

            med.IsDeleted    = true;
            med.LastUpdated  = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            _logger.LogInformation("Medicine '{Code}' ({Name}) soft-deleted by {User}",
                code, med.Name, User.Identity?.Name);

            return Ok(new { message = "Medicine removed from catalog" });
        }

        [HttpGet("categories")]
        public async Task<ActionResult<IEnumerable<string>>> GetCategories()
        {
            var medCats = await _context.Medicines
                .Select(m => m.Category)
                .Where(c => c != "")
                .Distinct()
                .ToListAsync();

            var dbCats = await _context.Categories
                .Select(c => c.Name)
                .ToListAsync();

            return medCats.Union(dbCats).OrderBy(c => c).ToList();
        }

        [HttpGet("low-stock")]
        public async Task<IActionResult> GetLowStock(
            [FromQuery] int threshold = 20,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            if (threshold < 1 || threshold > 1000) threshold = 20;
            if (page < 1) page = 1;
            if (page > 100000) page = 100000;
            if (pageSize < 1 || pageSize > 100) pageSize = 50;

            var query = _context.Medicines.Where(m => m.Stock < threshold);
            var total = await query.CountAsync();
            var items = await query
                .OrderBy(m => m.Stock)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { items, total, page, pageSize });
        }

        [HttpPut("{code}/stock")]
        public async Task<IActionResult> AdjustStock(string code, [FromBody] StockAdjustRequest req)
        {
            if (req.Adjustment < -100_000 || req.Adjustment > 100_000)
                return BadRequest(new { message = "Adjustment must be between -100,000 and 100,000" });

            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var med = await _context.Medicines.FirstOrDefaultAsync(m => m.Code == code);
                if (med == null) return NotFound();
                if (med.Stock + req.Adjustment < 0)
                    return BadRequest(new { message = "Stock cannot go below zero" });
                if (med.Stock + req.Adjustment > 100_000)
                    return BadRequest(new { message = "Adjusted stock cannot exceed 100,000" });

                var oldStock = med.Stock;
                med.Stock += req.Adjustment;
                med.LastUpdated = DateTime.UtcNow;
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation(
                    "Stock adjustment for '{Code}' by {User}: {Old} -> {New} (delta {Delta})",
                    code, User.Identity?.Name, oldStock, med.Stock, req.Adjustment);

                return Ok(new { code = med.Code, newStock = med.Stock });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _logger.LogError(ex, "Failed to adjust stock for '{Code}'", code);
                return StatusCode(500, new { message = "Failed to adjust stock. Please try again." });
            }
        }
    }

    public record StockAdjustRequest(int Adjustment);
}
