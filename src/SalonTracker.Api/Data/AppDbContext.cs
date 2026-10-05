using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;

namespace SalonTracker.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string OneActiveSessionIndex = "ix_sessions_one_active_per_employee";

    public DbSet<User> Users => Set<User>();
    public DbSet<Service> Services => Set<Service>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<SessionItem> SessionItems => Set<SessionItem>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(user =>
        {
            user.HasIndex(u => u.Username).IsUnique();
            user.Property(u => u.Name).HasMaxLength(60);
            user.Property(u => u.Username).HasMaxLength(60);
            user.Property(u => u.Locale).HasMaxLength(2);
            user.Property(u => u.Role).HasConversion(UpperCase<Role>()).HasMaxLength(16);
        });

        model.Entity<Service>(service =>
        {
            service.Property(s => s.NameTr).HasMaxLength(80);
            service.Property(s => s.NameDe).HasMaxLength(80);
        });

        model.Entity<Session>(session =>
        {
            session.Property(s => s.Status).HasConversion(UpperCase<SessionStatus>()).HasMaxLength(16);
            session.HasIndex(s => new { s.EmployeeId, s.Status });
            session.HasIndex(s => s.StartedAt);

            // One open customer per employee, guaranteed by the database: the API checks
            // too, but a double tap on a slow tablet could race past that check.
            session.HasIndex(s => s.EmployeeId, OneActiveSessionIndex)
                .HasDatabaseName(OneActiveSessionIndex)
                .IsUnique()
                .HasFilter("status = 'ACTIVE'");

            session.HasOne(s => s.Employee)
                .WithMany(u => u.Sessions)
                .HasForeignKey(s => s.EmployeeId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<SessionItem>(item =>
        {
            item.Property(i => i.Note).HasMaxLength(60);
            item.HasOne(i => i.Session)
                .WithMany(s => s.Items)
                .HasForeignKey(i => i.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            item.HasOne(i => i.Service)
                .WithMany()
                .HasForeignKey(i => i.ServiceId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    /// <summary>Enums are stored the way the API and the backups spell them: ADMIN, COMPLETED, ...</summary>
    private static ValueConverter<TEnum, string> UpperCase<TEnum>()
        where TEnum : struct, Enum =>
        new(v => v.ToString().ToUpperInvariant(), v => Enum.Parse<TEnum>(v, true));

    public static bool IsUniqueViolation(DbUpdateException e, string? constraint = null) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || pg.ConstraintName == constraint);
}
