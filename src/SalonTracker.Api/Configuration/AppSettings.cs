using Npgsql;

namespace SalonTracker.Api.Configuration;

/// <summary>
/// Everything the app reads from its environment, in one place. The variable names
/// match the Node version of the app, so the same <c>.env</c> and compose file work.
/// </summary>
public sealed class AppSettings
{
    public required string ConnectionString { get; init; }

    /// <summary>The same database as a libpq URI, which is what pg_dump understands.</summary>
    public required string PostgresUri { get; init; }

    public required string JwtSecret { get; init; }

    public required TimeZoneInfo SalonTimeZone { get; init; }

    public required string BackupDir { get; init; }

    public string PgDumpPath { get; init; } = "pg_dump";

    /// <summary>The built web app; served when the folder exists (production).</summary>
    public string? WebDist { get; init; }

    public bool BackupSchedulerEnabled { get; init; } = true;

    public SmtpSettings Smtp { get; init; } = new();

    public static AppSettings From(IConfiguration config)
    {
        var databaseUrl = Required(config, "DATABASE_URL");
        var connection = ToConnectionString(databaseUrl);

        return new AppSettings
        {
            ConnectionString = connection.ConnectionString,
            PostgresUri = ToPostgresUri(databaseUrl, connection),
            JwtSecret = Required(config, "JWT_SECRET"),
            SalonTimeZone = TimeZoneInfo.FindSystemTimeZoneById(config["SALON_TZ"] ?? "Europe/Berlin"),
            BackupDir = Path.GetFullPath(config["BACKUP_DIR"] ?? "backups"),
            PgDumpPath = config["PG_DUMP_PATH"] ?? "pg_dump",
            WebDist = config["WEB_DIST"] ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"),
            BackupSchedulerEnabled = !string.Equals(config["BACKUP_SCHEDULER"], "off", StringComparison.OrdinalIgnoreCase),
            Smtp = new SmtpSettings
            {
                Host = config["SMTP_HOST"] ?? "",
                Port = int.TryParse(config["SMTP_PORT"], out var port) ? port : 587,
                User = config["SMTP_USER"] ?? "",
                Password = config["SMTP_PASS"] ?? "",
                From = config["MAIL_FROM"] ?? "",
                AlertTo = config["ALERT_TO"] ?? "",
            },
        };
    }

    private static string Required(IConfiguration config, string key) =>
        string.IsNullOrWhiteSpace(config[key])
            ? throw new InvalidOperationException($"{key} environment variable is required")
            : config[key]!;

    /// <summary>Accepts both <c>postgresql://user:pass@host:port/db</c> and Npgsql key=value strings.</summary>
    internal static NpgsqlConnectionStringBuilder ToConnectionString(string databaseUrl)
    {
        if (!databaseUrl.StartsWith("postgres", StringComparison.OrdinalIgnoreCase))
        {
            return new NpgsqlConnectionStringBuilder(databaseUrl);
        }

        var uri = new Uri(databaseUrl);
        var credentials = uri.UserInfo.Split(':', 2);
        return new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(credentials[0]),
            Password = credentials.Length > 1 ? Uri.UnescapeDataString(credentials[1]) : null,
            Database = uri.AbsolutePath.TrimStart('/'),
        };
    }

    private static string ToPostgresUri(string databaseUrl, NpgsqlConnectionStringBuilder c) =>
        databaseUrl.StartsWith("postgres", StringComparison.OrdinalIgnoreCase)
            ? databaseUrl
            : $"postgresql://{Uri.EscapeDataString(c.Username ?? "")}:{Uri.EscapeDataString(c.Password ?? "")}@{c.Host}:{c.Port}/{c.Database}";
}

public sealed class SmtpSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 587;
    public string User { get; init; } = "";
    public string Password { get; init; } = "";
    public string From { get; init; } = "";
    public string AlertTo { get; init; } = "";

    /// <summary>Mail is optional: without these the system runs, just without mail.</summary>
    public bool Configured => Host != "" && User != "" && Password != "" && AlertTo != "";
}
