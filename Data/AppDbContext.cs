using Microsoft.EntityFrameworkCore;
using Nexffy.Models;

namespace Nexffy.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<Medicine> Medicines { get; set; }
        public DbSet<Bill> Bills { get; set; }
        public DbSet<BillItem> BillItems { get; set; }
        public DbSet<Category> Categories { get; set; }
        public DbSet<AppSetting> AppSettings { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Global filter: soft-deleted medicines never appear in normal queries
            modelBuilder.Entity<Medicine>().HasQueryFilter(m => !m.IsDeleted);

            // Indexes for common query patterns
            modelBuilder.Entity<Bill>()
                .HasIndex(b => new { b.Date, b.Id })
                .HasDatabaseName("IX_Bills_Date_Id");
            modelBuilder.Entity<Bill>()
                .HasIndex(b => b.Status)
                .HasDatabaseName("IX_Bills_Status");
            modelBuilder.Entity<Bill>()
                .HasIndex(b => b.PatientName)
                .HasDatabaseName("IX_Bills_PatientName");
            modelBuilder.Entity<BillItem>()
                .HasIndex(i => i.MedicineCode)
                .HasDatabaseName("IX_BillItems_MedicineCode");

            modelBuilder.Entity<Medicine>().HasData(
                new Medicine { Code = "MED-001", Name = "Panadol 500mg",     GenericName = "Paracetamol",              Category = "Painkiller",     Unit = "Strip",  Price = 85.00m,  Stock = 500,  ExpiryDate = "2027-06-30", Manufacturer = "GSK"       },
                new Medicine { Code = "MED-002", Name = "Augmentin 625mg",   GenericName = "Amoxicillin+Clavulanate", Category = "Antibiotic",     Unit = "Strip",  Price = 420.00m, Stock = 200,  ExpiryDate = "2027-03-31", Manufacturer = "GSK"       },
                new Medicine { Code = "MED-003", Name = "Brufen 400mg",      GenericName = "Ibuprofen",               Category = "Painkiller",     Unit = "Strip",  Price = 95.00m,  Stock = 350,  ExpiryDate = "2026-12-31", Manufacturer = "Abbott"    },
                new Medicine { Code = "MED-004", Name = "ORS Sachets",       GenericName = "Oral Rehydration Salts",  Category = "Rehydration",    Unit = "Sachet", Price = 20.00m,  Stock = 1000, ExpiryDate = "2028-01-31", Manufacturer = "Sanofi"    },
                new Medicine { Code = "MED-005", Name = "Disprin 300mg",     GenericName = "Aspirin",                 Category = "Painkiller",     Unit = "Strip",  Price = 45.00m,  Stock = 400,  ExpiryDate = "2027-09-30", Manufacturer = "Reckitt"   },
                new Medicine { Code = "MED-006", Name = "Risek 20mg",        GenericName = "Omeprazole",              Category = "Antacid",        Unit = "Strip",  Price = 185.00m, Stock = 250,  ExpiryDate = "2027-04-30", Manufacturer = "Getz"      },
                new Medicine { Code = "MED-007", Name = "Zyrtec 10mg",       GenericName = "Cetirizine",              Category = "Antihistamine",  Unit = "Strip",  Price = 120.00m, Stock = 300,  ExpiryDate = "2027-08-31", Manufacturer = "GSK"       },
                new Medicine { Code = "MED-008", Name = "Flagyl 400mg",      GenericName = "Metronidazole",           Category = "Antibiotic",     Unit = "Strip",  Price = 75.00m,  Stock = 180,  ExpiryDate = "2026-11-30", Manufacturer = "Sanofi"    },
                new Medicine { Code = "MED-009", Name = "Vitamin C 500mg",   GenericName = "Ascorbic Acid",           Category = "Vitamin",        Unit = "Bottle", Price = 250.00m, Stock = 150,  ExpiryDate = "2028-06-30", Manufacturer = "Pharmatec" },
                new Medicine { Code = "MED-010", Name = "Calpol Syrup 120ml",GenericName = "Paracetamol",             Category = "Painkiller",     Unit = "Bottle", Price = 110.00m, Stock = 200,  ExpiryDate = "2027-02-28", Manufacturer = "GSK"       }
            );
        }
    }
}
