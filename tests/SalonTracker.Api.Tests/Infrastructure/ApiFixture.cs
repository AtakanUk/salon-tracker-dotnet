using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using Testcontainers.PostgreSql;

namespace SalonTracker.Api.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "api";
}

/// <summary>
/// One app and one real PostgreSQL for all API tests. The database is either
/// <c>TEST_DATABASE_URL</c> (its name must end in <c>_test</c>, because every test
/// truncates it) or a throwaway Testcontainers PostgreSQL 17.
/// </summary>
public sealed class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public TestClock Clock { get; } = new();

    public string BackupDir { get; } = Path.Combine(Path.GetTempPath(), $"salon-tracker-tests-{Guid.NewGuid():N}");

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        // the app applies its migrations when it boots
        var connectionString = await ExistingDatabaseAsync() ?? await ContainerDatabaseAsync();

        Directory.CreateDirectory(BackupDir);
        Factory = new SalonApiFactory(connectionString, BackupDir, Clock);
        _ = Factory.Server;
    }

    /// <summary><c>TEST_DATABASE_URL</c>, dropped first so every run starts from nothing.</summary>
    private static async Task<string?> ExistingDatabaseAsync()
    {
        if (Environment.GetEnvironmentVariable("TEST_DATABASE_URL") is not { Length: > 0 } url) return null;

        var builder = AppSettings.ToConnectionString(url);
        if (builder.Database is not { } name || !name.EndsWith("_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Refusing to run tests against \"{builder.Database}\": the name must end in _test");
        }

        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString).UseSnakeCaseNamingConvention().Options);
        await db.Database.EnsureDeletedAsync();
        return builder.ConnectionString;
    }

    /// <summary>A throwaway PostgreSQL 17; it starts empty, so there is nothing to drop.</summary>
    private async Task<string> ContainerDatabaseAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("salon_tracker_test").Build();
        await _container.StartAsync();
        return _container.GetConnectionString();
    }

    /// <summary>Every test starts with empty tables and the real clock.</summary>
    public async Task ResetAsync()
    {
        Clock.Offset = TimeSpan.Zero;
        foreach (var file in Directory.EnumerateFiles(BackupDir)) File.Delete(file);

        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlRawAsync("TRUNCATE session_items, sessions, services, users RESTART IDENTITY CASCADE");
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
        if (Directory.Exists(BackupDir)) Directory.Delete(BackupDir, recursive: true);
    }

    private sealed class SalonApiFactory(string connectionString, string backupDir, TestClock clock)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DATABASE_URL", connectionString);
            builder.UseSetting("JWT_SECRET", "test-secret");
            builder.UseSetting("SALON_TZ", "Europe/Berlin");
            builder.UseSetting("BACKUP_DIR", backupDir);
            builder.UseSetting("BACKUP_SCHEDULER", "off");
            builder.UseSetting("WEB_DIST", Path.Combine(backupDir, "no-web-app"));
            builder.UseSetting("PG_DUMP_PATH", "pg_dump-is-not-installed-in-tests");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        }
    }
}

/// <summary>The real clock, shifted by <see cref="Offset"/> - "31 minutes later" without waiting.</summary>
public sealed class TestClock : TimeProvider
{
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
}
