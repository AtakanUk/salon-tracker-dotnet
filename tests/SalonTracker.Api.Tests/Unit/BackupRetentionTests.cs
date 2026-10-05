using SalonTracker.Api.Backup;

namespace SalonTracker.Api.Tests.Unit;

public sealed class BackupRetentionTests
{
    private static IEnumerable<string> Set(string day) =>
        [$"friseur-{day}.dump", $"friseur-{day}.xlsx", $"friseur-{day}.json.gz"];

    [Fact]
    public void Keeps_the_last_60_days_and_the_oldest_backup_of_every_month()
    {
        var files = new[] { "2026-05-01", "2026-05-02", "2026-05-31", "2026-06-03", "2026-06-04", "2026-08-01" }
            .SelectMany(Set)
            .Append(BackupFiles.LastBackupFile)
            .ToList();

        var expired = BackupRetention.Expired(files, cutoffDay: "2026-07-01");

        Assert.Equal(
            Set("2026-05-02").Concat(Set("2026-05-31")).Concat(Set("2026-06-04")).Order(),
            expired.Order());
    }

    [Fact]
    public void A_month_whose_first_day_is_missing_still_keeps_one_backup()
    {
        // the server was off on the 1st; the 5th becomes that month's keeper
        var files = new[] { "2026-04-05", "2026-04-20" }.SelectMany(Set);

        var expired = BackupRetention.Expired(files, cutoffDay: "2026-07-01");

        Assert.Equal(Set("2026-04-20").Order(), expired.Order());
    }

    [Fact]
    public void Leaves_files_it_did_not_write_alone()
    {
        var files = new[] { "pre-restore-20260501-1000.dump", "notes.txt", "friseur-2026-01-01.zip" };

        Assert.Empty(BackupRetention.Expired(files, cutoffDay: "2026-07-01"));
    }
}
