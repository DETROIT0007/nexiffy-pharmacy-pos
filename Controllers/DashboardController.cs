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
    public class DashboardController : ControllerBase
    {
        private readonly AppDbContext _context;
        public DashboardController(AppDbContext context) { _context = context; }

        [HttpGet]
        public async Task<IActionResult> GetStats([FromQuery] string period = "today")
        {
            var now = DateTime.Now;
            var today = now.ToString("yyyy-MM-dd");
            var thirtyDaysStr  = now.AddDays(30).ToString("yyyy-MM-dd");
            var fourteenDaysAgo = now.AddDays(-13).ToString("yyyy-MM-dd");

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

            // ── Daily revenue + bill counts — last 7 days ─────────
            var sevenDaysAgo = now.AddDays(-6).ToString("yyyy-MM-dd");
            var dailyRevenue = await _context.Bills
                .Where(b => b.Status != BillStatus.Cancelled &&
                            string.Compare(b.Date, sevenDaysAgo) >= 0 &&
                            string.Compare(b.Date, today) <= 0)
                .GroupBy(b => b.Date)
                .Select(g => new { date = g.Key, revenue = g.Sum(b => b.TotalAmount) })
                .OrderBy(x => x.date)
                .ToListAsync();

            var dailyBillCounts = await _context.Bills
                .Where(b => b.Status != BillStatus.Cancelled &&
                            string.Compare(b.Date, sevenDaysAgo) >= 0 &&
                            string.Compare(b.Date, today) <= 0)
                .GroupBy(b => b.Date)
                .Select(g => new { date = g.Key, count = g.Count() })
                .OrderBy(x => x.date)
                .ToListAsync();

            // ── Previous week revenue (ghost bar comparison) ───────
            var previousWeekRevenue = await _context.Bills
                .Where(b => b.Status != BillStatus.Cancelled &&
                            string.Compare(b.Date, fourteenDaysAgo) >= 0 &&
                            string.Compare(b.Date, sevenDaysAgo) < 0)
                .GroupBy(b => b.Date)
                .Select(g => new { date = g.Key, revenue = g.Sum(b => b.TotalAmount) })
                .OrderBy(x => x.date)
                .ToListAsync();

            // ── Bill status breakdown (period) ─────────────────────
            var cancelledCount = await _context.Bills.CountAsync(b =>
                b.Status == BillStatus.Cancelled &&
                string.Compare(b.Date, startDate) >= 0 &&
                string.Compare(b.Date, today) <= 0);

            // ── Sales by category — GROUP BY in SQL, category lookup in memory ──
            // Groups by medicine code in SQL (one row per code) instead of loading all items
            var revByCode = await periodQuery
                .SelectMany(b => b.Items)
                .GroupBy(i => i.MedicineCode)
                .Select(g => new { code = g.Key, revenue = g.Sum(i => i.Amount) })
                .ToListAsync();

            var soldCodes = revByCode.Select(x => x.code).ToList();
            var catMap = await _context.Medicines.IgnoreQueryFilters()
                .Where(m => soldCodes.Contains(m.Code))
                .Select(m => new { m.Code, Cat = m.Category == null || m.Category == "" ? "Other" : m.Category })
                .ToDictionaryAsync(m => m.Code, m => m.Cat);

            var salesByCategory = revByCode
                .GroupBy(x => catMap.TryGetValue(x.code, out var cat) ? cat : "Other")
                .Select(g => new { category = g.Key, revenue = Math.Round(g.Sum(x => x.revenue), 2) })
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
                    string.Compare(m.ExpiryDate, thirtyDaysStr) <= 0)
                .OrderBy(m => m.ExpiryDate)
                .Take(50)
                .Select(m => new { m.Name, m.ExpiryDate, m.Stock, m.Code })
                .ToListAsync();

            return Ok(new {
                period, startDate, today,
                periodSales, periodBillCount,
                totalRevenue, totalMedicines, lowStock, totalBills, inventoryValue,
                dailyRevenue, dailyBillCounts, previousWeekRevenue, salesByCategory,
                savedCount = periodBillCount, cancelledCount,
                expiryCalendar,
                topMedicines, recentBills, expiringMeds
            });
        }
    }
}
