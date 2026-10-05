using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Backup;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

public sealed record CleanupRequest(string Before, string Confirm);

public sealed class CleanupValidator : AbstractValidator<CleanupRequest>
{
    public CleanupValidator()
    {
        RuleFor(x => x.Before).Must(v => SalonTime.TryParseDay(v, out _));
        RuleFor(x => x.Confirm).Equal("DELETE");
    }
}

/// <summary>The owner's System page: health, backups, archives, restore and cleanup.</summary>
public static class SystemEndpoints
{
    /// <summary>
    /// Short on purpose: the backup must belong to this cleanup, not just be "some backup
    /// from earlier today". The panel takes one right before deleting.
    /// </summary>
    private static readonly TimeSpan FreshBackupAge = TimeSpan.FromMinutes(15);

    /// <summary>A salon's whole JSON export is well under a megabyte; this is a sanity cap.</summary>
    private const long MaxRestoreBytes = 64 * 1024 * 1024;

    public static void MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var system = app.MapGroup("/api/system").WithTags("System").RequireAuthorization(AuthSetup.AdminPolicy);

        system.MapGet("/status", Status);
        system.MapPost("/backup", async (BackupService backups) => new { result = await backups.RunAsync() });
        system.MapGet("/backup/latest.zip", LatestBackupZip);
        system.MapGet("/backup/file/{name}", BackupFile);
        system.MapGet("/archive/preview", async (string? from, string? to, AppDbContext db, AppSettings settings, TimeProvider time) =>
            await Summarize(db, ParseRange(from, to, settings, time).Range));
        system.MapGet("/archive.zip", ArchiveZip);
        system.MapPost("/restore", Restore);
        system.MapGet("/cleanup/preview", CleanupPreview);
        system.MapPost("/cleanup", Cleanup).Validate<CleanupRequest>();
        system.MapPost("/test-mail", TestMail);

        var export = app.MapGroup("/api/export").WithTags("System").RequireAuthorization(AuthSetup.AdminPolicy);
        export.MapGet("/sessions.xlsx", ExportSessions);
    }

    private static async Task<object> Status(
        AppDbContext db, AppSettings settings, BackupService backups, Mailer mailer, ErrorLog errors)
    {
        var dbOk = false;
        try
        {
            dbOk = await db.Database.CanConnectAsync();
        }
        catch (Exception)
        {
            // the database is down; that is what this card reports
        }

        return new
        {
            uptimeSeconds = (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds,
            dbOk,
            disk = DiskOf(settings.BackupDir),
            lastBackup = ReadLastBackup(settings.BackupDir),
            backupFiles = ListBackupFiles(settings.BackupDir),
            backupRunning = backups.IsRunning,
            mailConfigured = mailer.Configured,
            errors = errors.Recent(),
        };
    }

    /// <summary>One-tap download of the whole latest backup set. The files are already compressed, so they are stored as-is.</summary>
    private static async Task LatestBackupZip(HttpContext context, AppSettings settings)
    {
        var files = LatestBackupSet(settings.BackupDir);
        if (files.Count == 0) throw new ApiException(StatusCodes.Status404NotFound, "no_backup");

        var day = BackupFiles.DayOf(files[0]);
        await WriteZip(context, $"friseur-backup-{day}.zip", files.Select(name =>
            (name, (Func<Stream>)(() => File.OpenRead(Path.Combine(settings.BackupDir, name))))));
    }

    private static IResult BackupFile(string name, AppSettings settings)
    {
        // the pattern is the whole guard: no separators, no traversal, no other files
        if (!BackupFiles.Pattern().IsMatch(name)) throw ApiException.Validation();
        var path = Path.Combine(settings.BackupDir, name);
        if (!File.Exists(path)) throw ApiException.NotFound();
        return Results.File(path, "application/octet-stream", name);
    }

    /// <summary>
    /// The archive of a period: the same restorable JSON as the nightly backup, limited to
    /// the chosen days, plus its Excel sheet. Built on the fly and never stored, so it cannot
    /// be mistaken for the daily backup that guards the cleanup.
    /// </summary>
    private static async Task ArchiveZip(
        string? from, string? to, HttpContext context, AppDbContext db, AppSettings settings, TimeProvider time,
        JsonBackup json, ExcelExport excel)
    {
        var (range, label) = ParseRange(from, to, settings, time);
        if (!await range.Apply(db.Sessions).AnyAsync()) throw new ApiException(StatusCodes.Status404NotFound, "no_records");

        var name = $"friseur-archive-{label}";
        var jsonBytes = await json.ExportAsync(range);
        var xlsxBytes = await excel.BuildAsync(range);
        await WriteZip(context, $"{name}.zip",
        [
            ($"{name}.json.gz", () => new MemoryStream(jsonBytes)),
            ($"{name}.xlsx", () => new MemoryStream(xlsxBytes)),
        ]);
    }

    /// <summary>
    /// Restore from a JSON backup downloaded earlier (daily backup or period archive).
    /// Insert-only: it adds back what is missing and never overwrites or deletes.
    /// </summary>
    private static async Task<object> Restore(HttpContext context, JsonBackup json, ILogger<JsonBackup> logger)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxRestoreBytes;
        }

        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        if (body.Length == 0) throw new ApiException(StatusCodes.Status400BadRequest, "invalid_backup_file");

        var summary = await json.ImportAsync(JsonBackup.Parse(body.ToArray()));
        logger.LogWarning("Restore: {Added} rows added from the backup of {ExportedAt}", summary.Added.Total, summary.ExportedAt);
        return summary;
    }

    /// <summary>Deleting years of history is the most destructive thing the panel can do: preview first.</summary>
    private static async Task<object> CleanupPreview(string? before, AppDbContext db, AppSettings settings, TimeProvider time)
    {
        var cutoff = SalonTime.StartOfDay(SalonTime.ParseDay(before), settings.SalonTimeZone);
        var summary = await Summarize(db, new ExportRange(To: cutoff));
        return new
        {
            summary.SessionCount,
            summary.ItemCount,
            summary.RevenueCents,
            summary.OldestAt,
            summary.NewestAt,
            backupFresh = HasFreshBackup(settings.BackupDir, time),
        };
    }

    private static async Task<object> Cleanup(
        CleanupRequest body, AppDbContext db, AppSettings settings, TimeProvider time, ILogger<AppDbContext> logger)
    {
        // the panel makes the owner download a backup first; the server insists it exists
        if (!HasFreshBackup(settings.BackupDir, time)) throw new ApiException(StatusCodes.Status400BadRequest, "backup_required");

        var cutoff = SalonTime.StartOfDay(SalonTime.ParseDay(body.Before), settings.SalonTimeZone);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var items = await db.SessionItems.Where(i => i.Session.StartedAt < cutoff).ExecuteDeleteAsync();
        var sessions = await db.Sessions.Where(s => s.StartedAt < cutoff).ExecuteDeleteAsync();
        await transaction.CommitAsync();

        logger.LogWarning("Cleanup: deleted {Sessions} sessions before {Before}", sessions, body.Before);
        return new { deletedSessions = sessions, deletedItems = items };
    }

    private static async Task<object> TestMail(Mailer mailer)
    {
        if (!mailer.Configured) throw new ApiException(StatusCodes.Status400BadRequest, "mail_not_configured");
        var result = await mailer.SendAsync(
            "Friseur – test mail", "This is a test mail. Alerts and backup mails will arrive at this address.");
        if (!result.Sent) throw new ApiException(StatusCodes.Status502BadGateway, "mail_send_failed");
        return new { ok = true };
    }

    private static async Task<IResult> ExportSessions(
        string? from, string? to, AppSettings settings, ExcelExport excel)
    {
        var zone = settings.SalonTimeZone;
        var start = SalonTime.ParseDay(from);
        var end = SalonTime.ParseDay(to);
        var bytes = await excel.BuildAsync(new ExportRange(SalonTime.StartOfDay(start, zone), SalonTime.StartOfDay(end.AddDays(1), zone)));
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"friseur-{from}_{to}.xlsx");
    }

    /// <summary>?from=&amp;to= (both optional, salon dates) -> a UTC window and a name for the file.</summary>
    private static (ExportRange Range, string Label) ParseRange(string? from, string? to, AppSettings settings, TimeProvider time)
    {
        var zone = settings.SalonTimeZone;
        DateOnly? first = from is null ? null : SalonTime.ParseDay(from);
        DateOnly? last = to is null ? null : SalonTime.ParseDay(to);
        if (first > last) throw new ApiException(StatusCodes.Status400BadRequest, "range_reversed");

        var today = SalonTime.Format(SalonTime.DayOf(time.GetUtcNow().UtcDateTime, zone));
        return (
            new ExportRange(
                first is { } f ? SalonTime.StartOfDay(f, zone) : null,
                // the last day belongs to the range, so cut at the start of the next one
                last is { } l ? SalonTime.StartOfDay(l.AddDays(1), zone) : null),
            $"{from ?? "start"}_{to ?? today}");
    }

    /// <summary>What a window holds - shown before archiving or deleting it.</summary>
    private static async Task<RangeSummary> Summarize(AppDbContext db, ExportRange range)
    {
        var sessions = await range.Apply(db.Sessions)
            .OrderBy(s => s.StartedAt)
            .Select(s => new
            {
                s.Status,
                s.StartedAt,
                Items = s.Items.Count,
                Total = s.Items.Sum(i => i.PriceCentsSnapshot * i.Quantity),
            })
            .ToListAsync();

        return new RangeSummary(
            sessions.Count,
            sessions.Sum(s => s.Items),
            sessions.Where(s => s.Status == SessionStatus.Completed).Sum(s => s.Total),
            sessions.FirstOrDefault()?.StartedAt,
            sessions.LastOrDefault()?.StartedAt);
    }

    private sealed record RangeSummary(int SessionCount, int ItemCount, int RevenueCents, DateTime? OldestAt, DateTime? NewestAt);

    private static async Task WriteZip(HttpContext context, string fileName, IEnumerable<(string Name, Func<Stream> Open)> entries)
    {
        context.Response.ContentType = "application/zip";
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";

        // ZipArchive writes synchronously; build it in memory (a backup set is a few hundred KB)
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, open) in entries)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                await using var target = entry.Open();
                await using var source = open();
                await source.CopyToAsync(target);
            }
        }
        buffer.Position = 0;
        await buffer.CopyToAsync(context.Response.Body);
    }

    /// <summary>The newest backup set: the last run's files, else the newest date in the folder.</summary>
    private static List<string> LatestBackupSet(string dir)
    {
        var last = ReadLastBackup(dir);
        if (last is { } doc && doc.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
        {
            var onDisk = files.EnumerateArray()
                .Select(f => f.GetString() ?? "")
                .Where(n => BackupFiles.Pattern().IsMatch(n) && File.Exists(Path.Combine(dir, n)))
                .ToList();
            if (onDisk.Count > 0) return onDisk;
        }

        if (!Directory.Exists(dir)) return [];
        var all = Directory.EnumerateFiles(dir)
            .Select(Path.GetFileName)
            .Where(n => BackupFiles.Pattern().IsMatch(n!))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (all.Count == 0) return [];
        var newestDay = BackupFiles.DayOf(all[^1]!);
        return all.Where(n => n!.StartsWith($"friseur-{newestDay}", StringComparison.Ordinal)).ToList()!;
    }

    /// <summary>A backup taken minutes ago - the safety net for the cleanup.</summary>
    private static bool HasFreshBackup(string dir, TimeProvider time) =>
        LatestBackupSet(dir).Any(name =>
            time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(Path.Combine(dir, name)) <= FreshBackupAge);

    private static JsonElement? ReadLastBackup(string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, BackupFiles.LastBackupFile)));
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<object> ListBackupFiles(string dir) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
                .Select(path => new FileInfo(path))
                .Where(f => f.Name.StartsWith("friseur-", StringComparison.Ordinal))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Take(9)
                .Select(f => new { name = f.Name, sizeKb = (long)Math.Round(f.Length / 1024.0, MidpointRounding.AwayFromZero) })
                .ToList()
            : [];

    /// <summary>Free and total space of the file system the backups live on.</summary>
    private static object? DiskOf(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir);
            var drive = DriveInfo.GetDrives()
                .Where(d => d.IsReady && full.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
                .MaxBy(d => d.RootDirectory.FullName.Length);
            return drive is null
                ? null
                : new { freeMb = drive.AvailableFreeSpace / 1_048_576, totalMb = drive.TotalSize / 1_048_576 };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
