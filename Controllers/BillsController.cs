using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexiffy.Data;
using Nexiffy.Models;
using System.Data;

namespace Nexiffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BillsController : ControllerBase
    {
        private readonly AppDbContext _context;
        private readonly ILogger<BillsController> _logger;

        public BillsController(AppDbContext context, ILogger<BillsController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(
            [FromQuery] string? date,
            [FromQuery] string? patient,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            if (page < 1) page = 1;
            if (page > 100000) page = 100000;
            if (pageSize < 1 || pageSize > 100) pageSize = 20;

            var query = _context.Bills.Include(b => b.Items).AsQueryable();

            if (!string.IsNullOrWhiteSpace(date))
                query = query.Where(b => b.Date == date);

            if (!string.IsNullOrWhiteSpace(patient))
                query = query.Where(b =>
                    b.PatientName.Contains(patient) ||
                    b.PatientCode.Contains(patient));

            var total = await query.CountAsync();
            var items = await query
                .OrderByDescending(b => b.Date)
                .ThenByDescending(b => b.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { items, total, page, pageSize });
        }

        [HttpGet("{id}")]
        public async Task<ActionResult<Bill>> GetOne(string id)
        {
            var bill = await _context.Bills.Include(b => b.Items)
                .FirstOrDefaultAsync(b => b.Id == id);
            return bill == null ? NotFound() : Ok(bill);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] Bill bill)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            if (bill.Items == null || bill.Items.Count == 0)
                return BadRequest(new { message = "Bill must have at least one item" });

            // Validate all item rates are positive
            foreach (var item in bill.Items)
            {
                if (item.Rate <= 0)
                    return BadRequest(new { message = $"Invalid rate for {item.MedicineName}" });
                if (item.Qty < 1 || item.Qty != Math.Floor(item.Qty))
                    return BadRequest(new { message = $"Quantity for {item.MedicineName} must be a positive whole number" });
            }

            await using var transaction = await _context.Database
                .BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                // Sequential bill ID — SERIALIZABLE lock prevents concurrent duplicates
                var lastId = await _context.Bills
                    .OrderByDescending(b => b.Id)
                    .Select(b => b.Id)
                    .FirstOrDefaultAsync();

                int seq = 1;
                if (lastId != null)
                {
                    if (lastId.StartsWith("BILL-") && int.TryParse(lastId[5..], out int parsed))
                        seq = parsed + 1;
                    else
                        _logger.LogError("Non-standard bill ID '{LastId}' found — ID sequence may be corrupted", lastId);
                }

                bill.Id = $"BILL-{seq:D8}";
                bill.Date = DateTime.UtcNow.ToString("yyyy-MM-dd");
                bill.Status = BillStatus.Saved;
                bill.CreatedBy = User.Identity?.Name;

                // Batch-load all medicines in one query to avoid N+1 inside the SERIALIZABLE transaction
                var itemCodes = bill.Items.Select(i => i.MedicineCode).Distinct().ToList();
                var medicines = await _context.Medicines
                    .Where(m => itemCodes.Contains(m.Code))
                    .ToDictionaryAsync(m => m.Code);

                foreach (var item in bill.Items)
                {
                    if (!medicines.TryGetValue(item.MedicineCode, out var med))
                        return BadRequest(new { message = $"Medicine '{item.MedicineCode}' not found" });

                    // Override client-supplied rate and amount with server-authoritative values
                    item.Rate   = med.Price;
                    item.Amount = Math.Round(item.Rate * item.Qty, 2);

                    var deduct = (int)item.Qty;
                    if (med.Stock < deduct)
                        return BadRequest(new { message = $"Insufficient stock for {med.Name}. Available: {med.Stock}" });

                    med.Stock -= deduct;
                    med.LastUpdated = DateTime.UtcNow;
                }

                // Subtotal computed after server-side rate/amount correction
                var subtotal = bill.Items.Sum(i => i.Amount);
                if (bill.Discount < 0 || bill.Discount > subtotal)
                    return BadRequest(new { message = "Discount cannot exceed subtotal" });

                bill.TotalAmount = subtotal - bill.Discount;

                _context.Bills.Add(bill);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Bill {BillId} created by {User} — {Items} items, total PKR {Total}",
                    bill.Id, User.Identity?.Name, bill.Items.Count, bill.TotalAmount);

                return Ok(new { message = "Bill saved", id = bill.Id, totalAmount = bill.TotalAmount });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                var cid = HttpContext.TraceIdentifier;
                _logger.LogError(ex, "[{CorrelationId}] Failed to create bill for user {User}", cid, User.Identity?.Name);
                return StatusCode(500, new { message = "Failed to save bill. Please try again.", correlationId = cid });
            }
        }

        [HttpPut("{id}/cancel")]
        public async Task<IActionResult> Cancel(string id, [FromBody] CancelRequest? req)
        {
            if (string.IsNullOrWhiteSpace(req?.Reason))
                return BadRequest(new { message = "A cancellation reason is required" });

            await using var transaction = await _context.Database
                .BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                // Evaluate outside the expression tree — ?. is not allowed inside lambdas
                var cancelNote = req.Reason;
                var cancelledBy = User.Identity?.Name;
                var today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                // Atomic status flip — prevents double-cancel under concurrent requests
                var updated = await _context.Bills
                    .Where(b => b.Id == id && b.Status != BillStatus.Cancelled && b.Date == today)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.Status, BillStatus.Cancelled)
                        .SetProperty(b => b.Notes, cancelNote)
                        .SetProperty(b => b.CancelledAt, DateTime.UtcNow)
                        .SetProperty(b => b.CancelledBy, cancelledBy));

                if (updated == 0)
                {
                    var existing = await _context.Bills
                        .Where(b => b.Id == id)
                        .Select(b => new { b.Status, b.Date })
                        .FirstOrDefaultAsync();

                    if (existing == null)
                        return NotFound(new { message = "Bill not found" });
                    if (existing.Status == BillStatus.Cancelled)
                        return BadRequest(new { message = "Bill already cancelled" });
                    return BadRequest(new { message = "Only bills from today can be cancelled" });
                }

                // Restore stock — IgnoreQueryFilters so soft-deleted medicines still get their stock back
                var bill = await _context.Bills.Include(b => b.Items)
                    .FirstOrDefaultAsync(b => b.Id == id);

                var cancelCodes = bill!.Items.Select(i => i.MedicineCode).Distinct().ToList();
                var cancelMeds = await _context.Medicines.IgnoreQueryFilters()
                    .Where(m => cancelCodes.Contains(m.Code))
                    .ToDictionaryAsync(m => m.Code);

                foreach (var item in bill.Items)
                {
                    if (cancelMeds.TryGetValue(item.MedicineCode, out var med))
                    {
                        med.Stock += (int)item.Qty;
                        med.LastUpdated = DateTime.UtcNow;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Stock restore skipped — medicine '{Code}' not found during cancel of bill {BillId}",
                            item.MedicineCode, id);
                    }
                }

                await _context.SaveChangesAsync();
                await transaction.CommitAsync();

                _logger.LogInformation("Bill {BillId} cancelled by {User}. Reason: {Reason}",
                    id, cancelledBy, cancelNote);

                return Ok(new { message = "Bill cancelled and stock restored", id });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                var cid = HttpContext.TraceIdentifier;
                _logger.LogError(ex, "[{CorrelationId}] Failed to cancel bill {BillId} for user {User}", cid, id, User.Identity?.Name);
                return StatusCode(500, new { message = "Failed to cancel bill. Please try again.", correlationId = cid });
            }
        }
    }

    public record CancelRequest(string? Reason);
}
