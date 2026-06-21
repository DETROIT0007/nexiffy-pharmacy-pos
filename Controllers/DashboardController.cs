using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexffy.Data;
using Nexffy.Models;

namespace Nexffy.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class DashboardController : ControllerBase
    {
        private readonly AppDbContext _context;
        public DashboardController(AppDbContext context) { _context = context; }

        [HttpGet]
        public async Task<IActionResult> GetStats([FromQuery] string period = "today")
        {
            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var ninetyDays = now.AddDays(90).ToString("yyyy-MM-dd");

            var startDate = period switch
            {
                "week"  => now.AddDays(-6).ToString("yyyy-MM-dd"),
                "month" => new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd"),
                _       => today
            };

            // ── Period-scoped saved bills ──────────────────────────
            var periodQuery = _context.Bills.Where(b =>
                b.Status != BillStatus.Cancelled &&
                string.Compare(b.Date, startDate) >= 0 &&
                string.Compare(b.Date, today) <= 0);

            var periodSales     = await periodQuery.SumAsync(b => (decimal?)b.TotalAmount) ?? 0m;
            var periodBillCount = await periodQuery.CountAsync();

            // ── All-time stats ─────────────────────────────────────
            var totalRevenue   = await _context.Bills.Where(b => b.Status != BillStatus.Cancelled).SumAsync(b => (decimal?)b.TotalAmount) ?? 0m;
            var totalMedicines = await _context.Medicines.CountAsync();
            var lowStock       = await _context.Medicines.CountAsync(m => m.Stock < 20);
            var totalBills     = await _context.Bills.CountAsync(b => b.Status != BillStatus.Cancelled);

            // Inventory value — load slim projection into memory to avoid EF Core decimal*int issues
            var medValues  = await _context.Medicines.Select(m => new { m.Price, m.Stock }).ToListAsync();
            var inventoryValue = medValues.Sum(m => m.Price * m.Stock);

            // ── Daily revenue — last 7 days ────────────────────────
            var sevenDaysAgo = now.AddDays(-6).ToString("yyyy-MM-dd");
            var dailyRevenue = await _context.Bills
                .Where(b => b.Status != BillStatus.Cancelled &&
                            string.Compare(b.Date, sevenDaysAgo) >= 0 &&
                            string.Compare(b.Date, today) <= 0)
                .GroupBy(b => b.Date)
                .Select(g => new { date = g.Key, revenue = g.Sum(b => b.TotalAmount) })
                .OrderBy(x => x.date)
                .ToListAsync();

            // ── Bill status breakdown (period) ─────────────────────
            var cancelledCount = await _context.Bills.CountAsync(b =>
                b.Status == BillStatus.Cancelled &&
                string.Compare(b.Date, startDate) >= 0 &&
                string.Compare(b.Date, today) <= 0);

            // ── Sales by category (period, in-memory join) ─────────
            var billItems = await periodQuery.SelectMany(b => b.Items).ToListAsync();
            var medCodes  = billItems.Select(i => i.MedicineCode).Distinct().ToList();
            var medCatMap = await _context.Medicines.IgnoreQueryFilters()
                .Where(m => medCodes.Contains(m.Code))
                .ToDictionaryAsync(m => m.Code, m => string.IsNullOrEmpty(m.Category) ? "Other" : m.Category);

            var salesByCategory = billItems
                .GroupBy(i => medCatMap.TryGetValue(i.MedicineCode, out var c) ? c : "Other")
                .Select(g => new { category = g.Key, revenue = Math.Round(g.Sum(i => i.Amount), 2) })
                .OrderByDescending(x => x.revenue)
                .Take(6)
                .ToList();

            // ── Expiry calendar — current month ────────────────────
            var monthStart = new DateTime(now.Year, now.Month, 1).ToString("yyyy-MM-dd");
            var monthEnd   = new DateTime(now.Year, now.Month, DateTime.DaysInMonth(now.Year, now.Month)).ToString("yyyy-MM-dd");
            var expiryCalendar = await _context.Medicines
                .Where(m => m.ExpiryDate != null &&
                            string.Compare(m.ExpiryDate, monthStart) >= 0 &&
                            string.Compare(m.ExpiryDate, monthEnd) <= 0)
                .GroupBy(m => m.ExpiryDate)
                .Select(g => new { date = g.Key, count = g.Count() })
                .OrderBy(x => x.date)
                .ToListAsync();

            // ── Top medicines (period) ─────────────────────────────
            var topMedicines = await periodQuery
                .SelectMany(b => b.Items)
                .GroupBy(i => new { i.MedicineCode, i.MedicineName })
                .Select(g => new { medicineName = g.Key.MedicineName, totalQty = g.Sum(i => i.Qty) })
                .OrderByDescending(x => x.totalQty)
                .Take(5)
                .ToListAsync();

            // ── Recent bills + expiring meds (always latest) ───────
            var recentBills = await _context.Bills
                .OrderByDescending(b => b.Date)
                .ThenByDescending(b => b.Id)
                .Take(6)
                .Select(b => new { b.Id, b.PatientName, b.Date, b.TotalAmount, b.Status })
                .ToListAsync();

            var expiringMeds = await _context.Medicines
                .Where(m => m.ExpiryDate != null &&
                    string.Compare(m.ExpiryDate, today) >= 0 &&
                    string.Compare(m.ExpiryDate, ninetyDays) <= 0)
                .OrderBy(m => m.ExpiryDate)
                .Take(5)
                .Select(m => new { m.Name, m.ExpiryDate, m.Stock })
                .ToListAsync();

            return Ok(new {
                period, startDate, today,
                periodSales, periodBillCount,
                totalRevenue, totalMedicines, lowStock, totalBills, inventoryValue,
                dailyRevenue, salesByCategory,
                savedCount = periodBillCount, cancelledCount,
                expiryCalendar,
                topMedicines, recentBills, expiringMeds
            });
        }
    }
}
