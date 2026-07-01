using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexiffy.Data;
using Nexiffy.Models;
using System.Text.Json;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MedicinesController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<MedicinesController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

        public MedicinesController(AppDbContext context, ILogger<MedicinesController> logger, IHttpClientFactory httpClientFactory)
        {
            _context = context;
            _logger = logger;
            _httpClientFactory = httpClientFactory;
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

            var query = _context.Medicines.Include(m => m.PackUnits).AsQueryable();

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
            var med = await _context.Medicines.Include(m => m.PackUnits).FirstOrDefaultAsync(m => m.Code == code);
            return med == null ? NotFound() : Ok(med);
        }

        [HttpGet("barcode/{barcode}")]
        public async Task<ActionResult<Medicine>> GetByBarcode(string barcode)
        {
            var med = await _context.Medicines.Include(m => m.PackUnits).FirstOrDefaultAsync(m => m.Barcode == barcode);
            return med == null ? NotFound() : Ok(med);
        }

        // Best-effort product-name lookup against a free, keyless public UPC/EAN
        // database, for pre-filling the Add Medicine form when scanning a
        // barcode with no local match yet. This is crowd-sourced consumer
        // product data, not a pharmaceutical registry — coverage of local/
        // regional medicine brands will be spotty, and occasional entries are
        // simply wrong. Only Name/Manufacturer are returned (the only fields
        // a generic product lookup can plausibly get right); the caller must
        // still let the user review before saving, never auto-save.
        [HttpGet("lookup-external/{barcode}")]
        public async Task<IActionResult> LookupExternal(string barcode)
        {
            if (string.IsNullOrWhiteSpace(barcode) || barcode.Length > 50)
                return BadRequest(new { message = "Invalid barcode" });

            try
            {
                var client = _httpClientFactory.CreateClient("BarcodeLookup");
                var res = await client.GetAsync(
                    $"https://api.upcitemdb.com/prod/trial/lookup?upc={Uri.EscapeDataString(barcode)}");
                if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    _logger.LogWarning(
                        "External barcode lookup rate-limited (free tier is 100/day, 6/min) for {Barcode}", barcode);
                    return NotFound();
                }
                if (!res.IsSuccessStatusCode) return NotFound();

                using var stream = await res.Content.ReadAsStreamAsync();
                using var doc = await JsonDocument.ParseAsync(stream);
                var root = doc.RootElement;

                if (!root.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
                    return NotFound();

                var item = items[0];
                var name = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var brand = item.TryGetProperty("brand", out var b) ? b.GetString() : null;

                if (string.IsNullOrWhiteSpace(name)) return NotFound();

                return Ok(new
                {
                    name = name.Length > 200 ? name[..200] : name,
                    manufacturer = string.IsNullOrWhiteSpace(brand) ? "" : (brand.Length > 200 ? brand[..200] : brand)
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "External barcode lookup failed for {Barcode}", barcode);
                return NotFound();
            }
        }

        // A pack unit name must be unique per medicine and can't duplicate the
        // base Unit (e.g. base "Tablet" and a pack unit also named "Tablet"
        // would be ambiguous when resolving a sale).
        private static string? ValidatePackUnits(Medicine medicine)
        {
            var names = medicine.PackUnits.Select(p => p.UnitName.Trim()).ToList();
            if (names.Any(n => n.Equals(medicine.Unit.Trim(), StringComparison.OrdinalIgnoreCase)))
                return "A pack unit name can't match the base Unit";
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                return "Pack unit names must be unique";
            return null;
        }

        [HttpPost]
        public async Task<ActionResult<Medicine>> Create(Medicine medicine)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (await _context.Medicines.IgnoreQueryFilters().AnyAsync(m => m.Code == medicine.Code))
                return Conflict(new { message = "Medicine code already exists" });

            var packErr = ValidatePackUnits(medicine);
            if (packErr != null) return BadRequest(new { message = packErr });

            medicine.LastUpdated = DateTime.UtcNow;
            foreach (var p in medicine.PackUnits) p.MedicineCode = medicine.Code;
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

            var packErr = ValidatePackUnits(medicine);
            if (packErr != null) return BadRequest(new { message = packErr });

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
            existing.Barcode      = medicine.Barcode;
            existing.LastUpdated  = DateTime.UtcNow;

            // Replace-all: simplest correct way to sync the pack unit list,
            // since the form always submits the full current set.
            await _context.MedicinePackUnits.Where(p => p.MedicineCode == code).ExecuteDeleteAsync();
            foreach (var p in medicine.PackUnits)
            {
                p.Id = 0;
                p.MedicineCode = code;
                _context.MedicinePackUnits.Add(p);
            }

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
