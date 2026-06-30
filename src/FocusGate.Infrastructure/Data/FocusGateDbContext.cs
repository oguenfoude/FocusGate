using FocusGate.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace FocusGate.Infrastructure.Data;

public class FocusGateDbContext : DbContext
{
    public string MachineId { get; set; } = string.Empty;

    public DbSet<Modem>             Modems              => Set<Modem>();
    public DbSet<SimCard>           SimCards            => Set<SimCard>();
    public DbSet<SmsRecord>         SmsRecords          => Set<SmsRecord>();
    public DbSet<User>              Users               => Set<User>();
    public DbSet<UserModem>         UserModems          => Set<UserModem>();
    public DbSet<BalanceHistory>    BalanceHistories    => Set<BalanceHistory>();
    public DbSet<WithdrawalRequest> WithdrawalRequests  => Set<WithdrawalRequest>();
    public DbSet<UserBalanceHistory> UserBalanceHistories => Set<UserBalanceHistory>();

    public FocusGateDbContext(DbContextOptions<FocusGateDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Modem>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.IMEI).HasMaxLength(20).IsRequired();
            e.HasIndex(m => m.IMEI).IsUnique();
            e.HasQueryFilter(m => m.ArchivedAt == null);
        });

        modelBuilder.Entity<SimCard>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.IMSI).HasMaxLength(20).IsRequired();

            e.HasOne(s => s.Modem)
             .WithMany(m => m.SimCards)
             .HasForeignKey(s => s.ModemId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(s => new { s.ModemId, s.IsActive });
            e.HasQueryFilter(s => s.ArchivedAt == null);
        });

        modelBuilder.Entity<SmsRecord>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.SenderNumber).HasMaxLength(20);

            e.HasOne(s => s.SimCard)
             .WithMany(sc => sc.SmsRecords)
             .HasForeignKey(s => s.SimCardId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(s => s.ArchivedAt == null);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(u => u.Id);
            e.Property(u => u.Username).HasMaxLength(50).IsRequired();
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Password).HasMaxLength(100).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(100);
            e.HasQueryFilter(u => u.ArchivedAt == null);
        });

        modelBuilder.Entity<UserModem>(e =>
        {
            e.HasKey(um => um.Id);
            e.HasIndex(um => new { um.UserId, um.ModemId }).IsUnique();

            e.HasOne(um => um.User)
             .WithMany(u => u.UserModems)
             .HasForeignKey(um => um.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(um => um.Modem)
             .WithMany()
             .HasForeignKey(um => um.ModemId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(um => um.ArchivedAt == null);
        });

        modelBuilder.Entity<BalanceHistory>(e =>
        {
            e.HasKey(b => b.Id);
            e.HasIndex(b => b.ModemId);
            e.HasIndex(b => b.UserId);
            e.HasIndex(b => b.RecordedAt);

            e.HasOne(b => b.SimCard)
             .WithMany()
             .HasForeignKey(b => b.SimCardId)
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired(false);

            e.HasOne(b => b.Modem)
             .WithMany()
             .HasForeignKey(b => b.ModemId)
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired(false);

            e.HasOne(b => b.User)
             .WithMany(u => u.BalanceHistories)
             .HasForeignKey(b => b.UserId)
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired(false);
            e.HasQueryFilter(b => b.ArchivedAt == null);
        });

        modelBuilder.Entity<WithdrawalRequest>(e =>
        {
            e.HasKey(w => w.Id);
            e.HasIndex(w => w.UserId);
            e.HasIndex(w => w.Status);
            e.HasIndex(w => w.RequestedAt);

            e.HasOne(w => w.User)
             .WithMany(u => u.WithdrawalRequests)
             .HasForeignKey(w => w.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(w => w.ProcessedByAdmin)
             .WithMany()
             .HasForeignKey(w => w.ProcessedByAdminId)
             .OnDelete(DeleteBehavior.SetNull)
             .IsRequired(false);

            e.HasQueryFilter(w => w.ArchivedAt == null);
        });

        modelBuilder.Entity<UserBalanceHistory>(e =>
        {
            e.HasKey(ub => ub.Id);
            e.HasIndex(ub => ub.UserId);
            e.HasIndex(ub => ub.RecordedAt);

            e.HasOne(ub => ub.User)
             .WithMany(u => u.UserBalanceHistories)
             .HasForeignKey(ub => ub.UserId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(ub => ub.SimCard)
             .WithMany()
             .HasForeignKey(ub => ub.SimCardId)
             .OnDelete(DeleteBehavior.Cascade)
             .IsRequired(false);

            e.HasQueryFilter(ub => ub.ArchivedAt == null);
        });

        base.OnModelCreating(modelBuilder);
    }

    public override int SaveChanges()
    {
        StampUpdatedAt();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampUpdatedAt();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void StampUpdatedAt()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified))
        {
            if (entry.Entity is Modem m)
            {
                m.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(m.MachineId))
                    m.MachineId = MachineId;
            }
            else if (entry.Entity is SimCard s)
            {
                s.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(s.MachineId))
                    s.MachineId = MachineId;
            }
            else if (entry.Entity is SmsRecord sms)
            {
                sms.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(sms.MachineId))
                    sms.MachineId = MachineId;
            }
            else if (entry.Entity is BalanceHistory bh)
            {
                bh.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(bh.MachineId))
                    bh.MachineId = MachineId;
            }
            else if (entry.Entity is User u)
            {
                u.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(u.MachineId))
                    u.MachineId = MachineId;
            }
            else if (entry.Entity is UserModem um)
            {
                um.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(um.MachineId))
                    um.MachineId = MachineId;
            }
            else if (entry.Entity is WithdrawalRequest wr)
            {
                wr.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(wr.MachineId))
                    wr.MachineId = MachineId;
            }
            else if (entry.Entity is UserBalanceHistory ubh)
            {
                ubh.UpdatedAt = now;
                if (entry.State == EntityState.Added && string.IsNullOrEmpty(ubh.MachineId))
                    ubh.MachineId = MachineId;
            }
        }
    }
}
