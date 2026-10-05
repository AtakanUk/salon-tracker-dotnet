using SalonTracker.Api.Data;

namespace SalonTracker.Api.Backup;

/// <summary>
/// A window of sessions for exports. Both ends are optional, so the same type covers
/// "everything", "everything before X" (cleanup) and "X..Y" (archive). <see cref="From"/>
/// is inclusive, <see cref="To"/> exclusive - pass the start of the day after the last one.
/// </summary>
public sealed record ExportRange(DateTime? From = null, DateTime? To = null)
{
    public static readonly ExportRange All = new();

    public bool IsBounded => From is not null || To is not null;

    public IQueryable<Session> Apply(IQueryable<Session> sessions)
    {
        if (From is { } from) sessions = sessions.Where(s => s.StartedAt >= from);
        if (To is { } to) sessions = sessions.Where(s => s.StartedAt < to);
        return sessions;
    }
}
