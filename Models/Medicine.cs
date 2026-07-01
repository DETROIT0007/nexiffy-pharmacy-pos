using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Nexiffy.Models
{
    public static class BillStatus
    {
        public const string Saved     = "Saved";
        public const string Cancelled = "Cancelled";
    }

    public class Medicine
    {
        [Key]
        [Required]
        [MaxLength(50)]
        [RegularExpression(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,48}[A-Za-z0-9])?$",
            ErrorMessage = "Code must start and end with alphanumeric and contain only letters, numbers, and hyphens")]
        public string Code { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string Name { get; set; } = string.Empty;

        [MaxLength(200)]
        public string GenericName { get; set; } = string.Empty;

        [MaxLength(100)]
        public string Category { get; set; } = string.Empty;

        [Required]
        [MaxLength(50)]
        public string Unit { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        [Range(0.01, 999999.99, ErrorMessage = "Price must be between 0.01 and 999,999.99")]
        public decimal Price { get; set; }

        [Range(0, 1000000)]
        public int Stock { get; set; }

        [MaxLength(20)]
        [RegularExpression(@"^\d{4}-\d{2}-\d{2}$", ErrorMessage = "Expiry date must be YYYY-MM-DD")]
        public string? ExpiryDate { get; set; }

        [MaxLength(200)]
        public string Manufacturer { get; set; } = string.Empty;

        public DateTime? LastUpdated { get; set; }

        public bool IsDeleted { get; set; } = false;
    }

    public class Bill
    {
        [Key]
        [MaxLength(20)]
        public string Id { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string PatientName { get; set; } = string.Empty;

        [MaxLength(50)]
        public string PatientCode { get; set; } = string.Empty;

        [MaxLength(10)]
        public string Date { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalAmount { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Range(0, 999999.99)]
        public decimal Discount { get; set; }

        [MaxLength(20)]
        public string Status { get; set; } = BillStatus.Saved;

        [MaxLength(500)]
        public string? Notes { get; set; }

        [MaxLength(100)]
        public string? CreatedBy { get; set; }

        public DateTime? CancelledAt { get; set; }

        [MaxLength(100)]
        public string? CancelledBy { get; set; }

        public List<BillItem> Items { get; set; } = new();
    }

    public class BillItem
    {
        public int Id { get; set; }

        [Required]
        [MaxLength(50)]
        public string MedicineCode { get; set; } = string.Empty;

        [Required]
        [MaxLength(200)]
        public string MedicineName { get; set; } = string.Empty;

        [MaxLength(50)]
        public string Unit { get; set; } = string.Empty;

        [Column(TypeName = "decimal(18,2)")]
        [Range(1, 100000, ErrorMessage = "Quantity must be between 1 and 100,000")]
        public decimal Qty { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        [Range(0.01, 999999.99, ErrorMessage = "Rate must be positive")]
        public decimal Rate { get; set; }

        [Column(TypeName = "decimal(18,2)")]
        public decimal Amount { get; set; }
    }

    public record LoginRequest(string Username, string Password);
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    public class Category
    {
        [Key]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
    }

    public class AppSetting
    {
        [Key]
        [MaxLength(100)]
        public string Key { get; set; } = string.Empty;

        [MaxLength(2000)]
        public string Value { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class RevokedToken
    {
        [Key]
        [MaxLength(200)]
        public string Jti { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
    }
}
