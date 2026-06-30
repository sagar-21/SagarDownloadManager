using DM.LicenseServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DM.LicenseServer.Data;

public class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<Customer>      Customers      => Set<Customer>();
    public DbSet<License>       Licenses       => Set<License>();
    public DbSet<Device>        Devices        => Set<Device>();
    public DbSet<ActivationLog> ActivationLogs => Set<ActivationLog>();
    public DbSet<AbuseFlag>     AbuseFlags     => Set<AbuseFlag>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Customer>(e =>
        {
            e.HasIndex(c => c.Email).IsUnique();
        });

        mb.Entity<License>(e =>
        {
            e.HasIndex(l => l.Key).IsUnique();
            e.Property(l => l.Status).HasConversion<string>();

            e.HasOne(l => l.Customer)
             .WithMany(c => c.Licenses)
             .HasForeignKey(l => l.CustomerId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<Device>(e =>
        {
            e.HasIndex(d => new { d.LicenseId, d.HardwareFingerprint });
            e.Property(d => d.Status).HasConversion<string>();
            e.Property(d => d.IntegrityStatus).HasConversion<string>();

            e.HasOne(d => d.License)
             .WithMany(l => l.Devices)
             .HasForeignKey(d => d.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<ActivationLog>(e =>
        {
            e.Property(a => a.Event).HasConversion<string>();

            e.HasOne(a => a.License)
             .WithMany(l => l.Logs)
             .HasForeignKey(a => a.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);

            // Device nav is optional — some events (admin revoke) have no device
            e.HasOne(a => a.Device)
             .WithMany(d => d.Logs)
             .HasForeignKey(a => a.DeviceId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
        });

        mb.Entity<AbuseFlag>(e =>
        {
            e.Property(f => f.Type).HasConversion<string>();

            e.HasOne(f => f.License)
             .WithMany(l => l.AbuseFlags)
             .HasForeignKey(f => f.LicenseId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(f => f.Device)
             .WithMany(d => d.AbuseFlags)
             .HasForeignKey(f => f.DeviceId)
             .IsRequired(false)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
