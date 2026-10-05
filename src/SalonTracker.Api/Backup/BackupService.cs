using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Backup;

public sealed record StepResult(
    bool Ok,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

public sealed record MailStatus(
    bool Sent,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Error = null);

/// <summary>What the last run did; stored as last-backup.json and shown on the System page.</summary>
public sealed record BackupResult(
    DateTime StartedAt,
    DateTime FinishedAt,
    bool Ok, // the data exports (Excel + JSON) succeeded
    IReadOnlyList<string> Files,
    StepResult Dump,
    StepResult Excel,
    StepResult Json,
    MailStatus Mail,
    int DeletedOld);

/// <summary>
/// The nightly (or on-demand) backup: pg_dump + Excel + JSON into the backup folder, prune
/// old files, mail the result. The steps are independent: a missing pg_dump binary on a
/// development machine does not stop the Excel and JSON exports.
/// </summary>
public sealed class BackupService(
    AppSettings settings,
    IServiceScopeFactory scopes,
    Mailer mailer,
    TimeProvider time,
    ILogger<BackupService> logger)
{
    private static readonly JsonSerializerOptions StatusJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public async Task<BackupResult> RunAsync(CancellationToken ct = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            throw new ApiException(StatusCodes.Status409Conflict, "backup_already_running");
        }

        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<BackupResult> RunCoreAsync(CancellationToken ct)
    {
        var startedAt = time.GetUtcNow().UtcDateTime;
        var dir = settings.BackupDir;
        Directory.CreateDirectory(dir);
        var day = SalonTime.Format(SalonTime.DayOf(startedAt, settings.SalonTimeZone));
        var dumpPath = Path.Combine(dir, $"friseur-{day}.dump");
        var xlsxPath = Path.Combine(dir, $"friseur-{day}.xlsx");
        var jsonPath = Path.Combine(dir, $"friseur-{day}.json.gz");

        var dump = await RunPgDumpAsync(dumpPath, ct);

        await using var scope = scopes.CreateAsyncScope();
        var excel = await Step("excel", async () =>
            await File.WriteAllBytesAsync(xlsxPath, await scope.ServiceProvider.GetRequiredService<ExcelExport>().BuildAsync(ExportRange.All), ct));
        var json = await Step("json", async () =>
            await File.WriteAllBytesAsync(jsonPath, await scope.ServiceProvider.GetRequiredService<JsonBackup>().ExportAsync(ExportRange.All), ct));

        var deletedOld = DeleteOldBackups(dir, startedAt);

        var produced = new[] { (dump, dumpPath), (excel, xlsxPath), (json, jsonPath) }
            .Where(p => p.Item1.Ok)
            .Select(p => p.Item2)
            .ToList();

        var ok = excel.Ok && json.Ok;
        var mail = mailer.Configured
            ? await mailer.SendAsync(
                $"Friseur backup – {day} ({(ok && dump.Ok ? "OK" : "PROBLEM")})",
                string.Join('\n',
                    $"Friseur backup report – {day}",
                    "",
                    $"Database dump: {Describe(dump, dumpPath)}",
                    $"Excel file:    {Describe(excel, xlsxPath)}",
                    $"JSON export:   {Describe(json, jsonPath)}",
                    $"Old files removed: {deletedOld}",
                    "",
                    $"The files are attached and kept on the server for {BackupRetention.RetentionDays} days;",
                    "one backup per month is kept for good."),
                produced)
            : new MailResult(false, "mail_not_configured");
        if (!mail.Sent) logger.LogInformation("Backup mail not sent: {Error}", mail.Error);

        var result = new BackupResult(
            startedAt,
            time.GetUtcNow().UtcDateTime,
            ok,
            produced.Select(Path.GetFileName).ToList()!,
            dump,
            excel,
            json,
            new MailStatus(mail.Sent, mail.Error),
            deletedOld);
        await File.WriteAllTextAsync(
            Path.Combine(dir, BackupFiles.LastBackupFile), JsonSerializer.Serialize(result, StatusJson), ct);
        logger.LogInformation("Backup finished: ok={Ok} dump={Dump} mail={Mail}", ok, dump.Ok, mail.Sent);
        return result;
    }

    private async Task<StepResult> Step(string name, Func<Task> action)
    {
        try
        {
            await action();
            return new StepResult(true);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Backup step {Step} failed", name);
            return new StepResult(false, e.Message);
        }
    }

    private async Task<StepResult> RunPgDumpAsync(string outPath, CancellationToken ct)
    {
        var info = new ProcessStartInfo(settings.PgDumpPath) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "--format=custom", "--no-owner", $"--dbname={settings.PostgresUri}", $"--file={outPath}" })
        {
            info.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(info)!;
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode == 0) return new StepResult(true);

            var error = $"pg_dump exit {process.ExitCode}: {stderr[..Math.Min(stderr.Length, 300)]}";
            logger.LogError("Backup: {Error}", error);
            return new StepResult(false, error);
        }
        catch (Win32Exception e)
        {
            // no pg_dump on this machine (typical in development)
            logger.LogError("Backup: pg_dump: {Error}", e.Message);
            return new StepResult(false, $"pg_dump: {e.Message}");
        }
    }

    private int DeleteOldBackups(string dir, DateTime now)
    {
        var cutoff = SalonTime.Format(SalonTime.DayOf(now.AddDays(-BackupRetention.RetentionDays), settings.SalonTimeZone));
        var expired = BackupRetention.Expired(Directory.EnumerateFiles(dir).Select(Path.GetFileName)!, cutoff);
        foreach (var name in expired) File.Delete(Path.Combine(dir, name));
        return expired.Count;
    }

    private static string Describe(StepResult step, string path) =>
        step.Ok ? $"OK ({(File.Exists(path) ? new FileInfo(path).Length / 1024 : 0)} KB)" : $"FAILED – {step.Error}";
}
