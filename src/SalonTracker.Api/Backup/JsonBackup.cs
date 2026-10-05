using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Backup;

// The rows of a JSON backup, spelled exactly like the Node version writes them, so a
// backup taken by either version can be restored into the other.

public sealed record UserRow(
    int Id, string Name, string Username, string PasswordHash, Role Role, string Locale, bool Active,
    DateTime CreatedAt, DateTime? DeletedAt);

public sealed record ServiceRow(
    int Id, string NameTr, string NameDe, int PriceCents, int SortOrder, bool Active, bool Custom, DateTime CreatedAt);

public sealed record SessionRow(
    int Id, int EmployeeId, DateTime StartedAt, DateTime? FinishedAt, SessionStatus Status, DateTime? EditedAt);

public sealed record SessionItemRow(
    int Id, int SessionId, int ServiceId, int PriceCentsSnapshot, int Quantity, string? Note);

public sealed record BackupRangeInfo(DateTime? From, DateTime? To);

public sealed record BackupFile(
    int Format,
    DateTime ExportedAt,
    BackupRangeInfo? Range,
    IReadOnlyList<UserRow> Users,
    IReadOnlyList<ServiceRow> Services,
    IReadOnlyList<SessionRow> Sessions,
    IReadOnlyList<SessionItemRow> SessionItems);

public sealed record TableCounts(int Users, int Services, int Sessions, int Items)
{
    public int Total => Users + Services + Sessions + Items;
}

public sealed record ImportSummary(DateTime ExportedAt, TableCounts Found, TableCounts Added);

/// <summary>
/// The machine-readable backup: every table as gzipped JSON. Unlike a pg_dump, which
/// replaces everything, it can be merged into a live database - only missing rows go in.
/// </summary>
public sealed class JsonBackup(AppDbContext db, TimeProvider time, ILogger<JsonBackup> logger)
{
    public const int Format = 1;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper), new MillisecondUtcConverter() },
    };

    /// <summary>
    /// With a range only the sessions of that window are exported - the archive of a
    /// period. Users and services always come along in full: they are the parents of
    /// those sessions, without them the file could not be restored, and both are tiny.
    /// </summary>
    public async Task<byte[]> ExportAsync(ExportRange range)
    {
        var sessions = range.Apply(db.Sessions.AsNoTracking());
        var file = new BackupFile(
            Format,
            time.GetUtcNow().UtcDateTime,
            range.IsBounded ? new BackupRangeInfo(range.From, range.To) : null,
            await db.Users.AsNoTracking().OrderBy(u => u.Id)
                .Select(u => new UserRow(u.Id, u.Name, u.Username, u.PasswordHash, u.Role, u.Locale, u.Active, u.CreatedAt, u.DeletedAt))
                .ToListAsync(),
            await db.Services.AsNoTracking().OrderBy(s => s.Id)
                .Select(s => new ServiceRow(s.Id, s.NameTr, s.NameDe, s.PriceCents, s.SortOrder, s.Active, s.Custom, s.CreatedAt))
                .ToListAsync(),
            await sessions.OrderBy(s => s.Id)
                .Select(s => new SessionRow(s.Id, s.EmployeeId, s.StartedAt, s.FinishedAt, s.Status, s.EditedAt))
                .ToListAsync(),
            await sessions.SelectMany(s => s.Items).OrderBy(i => i.Id)
                .Select(i => new SessionItemRow(i.Id, i.SessionId, i.ServiceId, i.PriceCentsSnapshot, i.Quantity, i.Note))
                .ToListAsync());

        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            await JsonSerializer.SerializeAsync(gzip, file, Json);
        }
        return output.ToArray();
    }

    /// <summary>
    /// Reads the file the owner picked. The messages matter: people hand in the whole
    /// downloaded zip, or the Excel file, or the .dump.
    /// </summary>
    public static BackupFile Parse(byte[] content)
    {
        // "PK": a zip - the downloaded backup archive, or the .xlsx inside it
        if (content is [0x50, 0x4b, ..]) throw new ApiException(StatusCodes.Status400BadRequest, "zip_selected");

        BackupFile? file;
        try
        {
            using var gzip = new GZipStream(new MemoryStream(content), CompressionMode.Decompress);
            file = JsonSerializer.Deserialize<BackupFile>(gzip, Json);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or NotSupportedException)
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_backup_file");
        }

        if (file is not { Format: Format, Users: not null, Services: not null, Sessions: not null, SessionItems: not null })
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "invalid_backup_file");
        }
        return file;
    }

    /// <summary>
    /// Inserts only the rows whose id is not in the database yet; existing rows are never
    /// changed or deleted. Worst case nothing happens - which is what makes it safe to
    /// offer as a button in the admin panel. Runs in one transaction.
    /// </summary>
    public async Task<ImportSummary> ImportAsync(BackupFile file)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)transaction.GetDbTransaction();

        // parents first: users and services, then sessions, then their items
        var users = await InsertMissing(connection, tx, file.Users,
            "INSERT INTO users (id, name, username, password_hash, role, locale, active, created_at, deleted_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT DO NOTHING",
            u => [u.Id, u.Name, u.Username, u.PasswordHash, Upper(u.Role), u.Locale, u.Active, Utc(u.CreatedAt), Utc(u.DeletedAt)]);
        var services = await InsertMissing(connection, tx, file.Services,
            "INSERT INTO services (id, name_tr, name_de, price_cents, sort_order, active, custom, created_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8) ON CONFLICT DO NOTHING",
            s => [s.Id, s.NameTr, s.NameDe, s.PriceCents, s.SortOrder, s.Active, s.Custom, Utc(s.CreatedAt)]);
        var sessions = await InsertMissing(connection, tx, file.Sessions,
            "INSERT INTO sessions (id, employee_id, started_at, finished_at, status, edited_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING",
            s => [s.Id, s.EmployeeId, Utc(s.StartedAt), Utc(s.FinishedAt), Upper(s.Status), Utc(s.EditedAt)]);
        var items = await InsertMissing(connection, tx, file.SessionItems,
            "INSERT INTO session_items (id, session_id, service_id, price_cents_snapshot, quantity, note) " +
            "VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING",
            i => [i.Id, i.SessionId, i.ServiceId, i.PriceCentsSnapshot, i.Quantity, i.Note]);

        // explicit ids bypass the identity sequences; move them past the restored rows
        await db.Database.ExecuteSqlRawAsync("""
            SELECT setval(pg_get_serial_sequence('users', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM users), 1));
            SELECT setval(pg_get_serial_sequence('services', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM services), 1));
            SELECT setval(pg_get_serial_sequence('sessions', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM sessions), 1));
            SELECT setval(pg_get_serial_sequence('session_items', 'id'), GREATEST((SELECT COALESCE(MAX(id), 1) FROM session_items), 1));
            """);
        await transaction.CommitAsync();

        var summary = new ImportSummary(
            file.ExportedAt,
            new TableCounts(file.Users.Count, file.Services.Count, file.Sessions.Count, file.SessionItems.Count),
            new TableCounts(users, services, sessions, items));
        logger.LogInformation(
            "Backup restore: {Found} rows in the file, {Added} missing rows added", summary.Found.Total, summary.Added.Total);
        return summary;
    }

    private static async Task<int> InsertMissing<T>(
        NpgsqlConnection connection, NpgsqlTransaction tx, IReadOnlyList<T> rows, string sql, Func<T, object?[]> values)
    {
        var inserted = 0;
        foreach (var chunk in rows.Chunk(500))
        {
            await using var batch = new NpgsqlBatch(connection, tx);
            foreach (var row in chunk)
            {
                var command = new NpgsqlBatchCommand(sql);
                foreach (var value in values(row)) command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
                batch.BatchCommands.Add(command);
            }
            await batch.ExecuteNonQueryAsync();
            inserted += batch.BatchCommands.Cast<NpgsqlBatchCommand>().Sum(c => c.RecordsAffected);
        }
        return inserted;
    }

    private static string Upper<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToUpperInvariant();

    private static object? Utc(DateTime? value) => value is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
}

/// <summary>Dates as the Node version writes them: UTC with milliseconds, "2026-07-14T09:00:00.000Z".</summary>
internal sealed class MillisecondUtcConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
}
