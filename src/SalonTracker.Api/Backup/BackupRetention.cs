using System.Text.RegularExpressions;

namespace SalonTracker.Api.Backup;

public static partial class BackupFiles
{
    /// <summary>The only file names the backup job produces - also the guard for downloads.</summary>
    [GeneratedRegex(@"^friseur-\d{4}-\d{2}-\d{2}\.(dump|xlsx|json\.gz)$")]
    public static partial Regex Pattern();

    public const string LastBackupFile = "last-backup.json";

    /// <summary>"friseur-2026-08-19.xlsx" -> "2026-08-19"</summary>
    public static string DayOf(string name) => name.Substring("friseur-".Length, 10);
}

public static class BackupRetention
{
    public const int RetentionDays = 60;

    /// <summary>
    /// Daily backups live for <see cref="RetentionDays"/>; one set from every month is kept for
    /// good, so a problem noticed months later still has a point to return to.
    /// The keeper is the oldest day still on disk for its month, not "the 1st": if the server
    /// was down that night there is no backup for it, and a rule tied to the 1st would throw
    /// the whole month away. Once the rest of the month is gone the keeper is the only day
    /// left, so later runs pick it again.
    /// </summary>
    public static IReadOnlyList<string> Expired(IEnumerable<string> names, string cutoffDay)
    {
        var backups = names.Where(n => BackupFiles.Pattern().IsMatch(n)).ToList();
        var keeperOfMonth = backups
            .Select(BackupFiles.DayOf)
            .GroupBy(day => day[..7])
            .ToDictionary(g => g.Key, g => g.Min(StringComparer.Ordinal)!);

        return backups
            .Where(name =>
            {
                var day = BackupFiles.DayOf(name);
                return string.CompareOrdinal(day, cutoffDay) < 0 && keeperOfMonth[day[..7]] != day;
            })
            .ToList();
    }
}
