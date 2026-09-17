using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Api.Models;

namespace NovaWalletLedger.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<WalletTransaction> Transactions => Set<WalletTransaction>();
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Wallet>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.CustomerId).IsRequired();
            b.HasIndex(w => w.CustomerId);
            b.ToTable(t => t.HasCheckConstraint("CK_Wallets_BalanceNonNegative", "\"BalanceKobo\" >= 0"));
        });

        modelBuilder.Entity<WalletTransaction>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.WalletId, t.CreatedAtUtc });
            b.HasIndex(t => t.IdempotencyKey);
        });

        modelBuilder.Entity<AuditLogEntry>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).ValueGeneratedOnAdd();
            b.HasIndex(a => a.WalletId);
        });

        modelBuilder.Entity<IdempotencyRecord>(b =>
        {
            b.HasKey(i => i.Key);
        });
    }
}
