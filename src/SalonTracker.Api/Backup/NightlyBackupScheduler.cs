using System.Text.Json;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Backup;

/// <summary>
/// Backs up every night at 03:00 salon time, plus a catch-up run shortly after start-up
/// when the last backup is stale - the server may have been off during the night.
/// </summary>
public sealed class NightlyBackupScheduler(
    BackupService backups,
    AppSettings settings,
    TimeProvider time,
    ILogger<NightlyBackupScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan BackupHour = TimeSpan.FromHours(3);
    private static readonly TimeSpan CatchUpAfter = TimeSpan.FromHours(25);
    private static readonly TimeSpan CatchUpDelay = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.BackupSchedulerEnabled) return;

        var catchUp = LastBackupAge() is not { } age || age > CatchUpAfter;
        if (catchUp) logger.LogInformation("Last backup is stale or missing, catch-up backup in 5 min");

        try
        {
            if (catchUp)
            {
                await Task.Delay(CatchUpDelay, time, stoppingToken);
                await RunSafely("catch-up");
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = UntilNextRun();
                logger.LogInformation("Next backup in {Minutes} min", (int)delay.TotalMinutes);
                await Task.Delay(delay, time, stoppingToken);
                await RunSafely("scheduled");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private async Task RunSafely(string kind)
    {
        try
        {
            await backups.RunAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "The {Kind} backup failed", kind);
        }
    }

    private TimeSpan UntilNextRun()
    {
        var now = time.GetUtcNow().UtcDateTime;
        var zone = settings.SalonTimeZone;
        var today = SalonTime.DayOf(now, zone);
        var runAt = SalonTime.StartOfDay(today, zone) + BackupHour;
        if (runAt <= now) runAt = SalonTime.StartOfDay(today.AddDays(1), zone) + BackupHour;
        return runAt - now;
    }

    private TimeSpan? LastBackupAge()
    {
        try
        {
            var path = Path.Combine(settings.BackupDir, BackupFiles.LastBackupFile);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return time.GetUtcNow().UtcDateTime - doc.RootElement.GetProperty("startedAt").GetDateTime().ToUniversalTime();
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or FormatException)
        {
            return null;
        }
    }
}
