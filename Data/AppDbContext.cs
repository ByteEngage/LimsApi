using LimsApi.Models;
using Microsoft.EntityFrameworkCore;

namespace LimsApi.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserModule> UserModules => Set<UserModule>();
    public DbSet<LabDomain> Domains => Set<LabDomain>();
    public DbSet<TestType> TestTypes => Set<TestType>();
    public DbSet<Parameter> Parameters => Set<Parameter>();
    public DbSet<Specification> Specifications => Set<Specification>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<SampleTest> SampleTests => Set<SampleTest>();
    public DbSet<Result> Results => Set<Result>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.PasswordHash).HasMaxLength(100);
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<UserModule>(e =>
        {
            e.Property(x => x.Module).HasMaxLength(30);
            e.HasIndex(x => new { x.UserId, x.Module }).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.ModuleAccess).HasForeignKey(x => x.UserId);
        });

        b.Entity<LabDomain>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<TestType>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => new { x.DomainId, x.Name }).IsUnique();
        });

        b.Entity<Parameter>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Unit).HasMaxLength(30);
            e.Property(x => x.ResultType).HasMaxLength(20);
            e.Property(x => x.Method).HasMaxLength(150);
            e.HasIndex(x => new { x.TestTypeId, x.Name }).IsUnique();
            e.HasOne(x => x.Specification).WithOne(s => s.Parameter).HasForeignKey<Specification>(s => s.ParameterId);
        });

        b.Entity<Specification>(e =>
        {
            e.Property(x => x.StandardName).HasMaxLength(150);
            e.Property(x => x.MinValue).HasPrecision(18, 4);
            e.Property(x => x.MaxValue).HasPrecision(18, 4);
        });

        // SQL Server rejects several cascade paths to the same table, and lab
        // records should never vanish because configuration was deleted.
        // So these links use Restrict (no cascade delete).
        b.Entity<Sample>(e =>
        {
            e.Property(x => x.SampleCode).HasMaxLength(30);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.ClientName).HasMaxLength(150);
            e.Property(x => x.Status).HasMaxLength(20);
            e.Property(x => x.RegisteredBy).HasMaxLength(50);
            e.Property(x => x.ReviewedBy).HasMaxLength(50);
            e.Property(x => x.QcBy).HasMaxLength(50);
            e.Property(x => x.ApprovedBy).HasMaxLength(50);
            e.Property(x => x.FinalizedBy).HasMaxLength(50);
            e.Property(x => x.ReportNumber).HasMaxLength(30);
            e.HasIndex(x => x.ReportNumber).IsUnique().HasFilter("[ReportNumber] IS NOT NULL");
            e.Property(x => x.ReviewNote).HasMaxLength(500);
            e.Property(x => x.QcNote).HasMaxLength(500);
            e.Property(x => x.ReturnNote).HasMaxLength(700);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.SampleCode).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SampleTest>(e =>
        {
            e.HasOne(x => x.TestType).WithMany().HasForeignKey(x => x.TestTypeId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Result>(e =>
        {
            e.Property(x => x.Value).HasPrecision(18, 4);
            e.Property(x => x.TextValue).HasMaxLength(20);
            e.Property(x => x.Method).HasMaxLength(150);
            e.Property(x => x.LowerLimit).HasPrecision(18, 4);
            e.Property(x => x.UpperLimit).HasPrecision(18, 4);
            e.Property(x => x.EnteredBy).HasMaxLength(50);
            e.HasOne(x => x.Parameter).WithMany().HasForeignKey(x => x.ParameterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.Action).HasMaxLength(50);
            e.Property(x => x.Entity).HasMaxLength(50);
            e.Property(x => x.EntityId).HasMaxLength(50);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.Property(x => x.SampleCode).HasMaxLength(30);
            e.Property(x => x.ReportNumber).HasMaxLength(30);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.SampleCode);
            e.HasIndex(x => x.ReportNumber);
        });
    }
}
