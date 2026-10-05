using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

/// <summary>
/// Owner statistics. Only finished records count, at the price they were saved with,
/// grouped by the salon's calendar rather than UTC.
/// </summary>
public static class StatsEndpoints
{
    public static void MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        var stats = app.MapGroup("/api/stats").WithTags("Statistics").RequireAuthorization(AuthSetup.AdminPolicy);
        stats.MapGet("/overview", Overview);
        stats.MapGet("/timeseries", Timeseries);
    }

    private static Task<List<Session>> CompletedIn(AppDbContext db, AppSettings settings, string? from, string? to)
    {
        var zone = settings.SalonTimeZone;
        var start = SalonTime.StartOfDay(SalonTime.ParseDay(from), zone);
        // the last day belongs to the range, so cut at the start of the next one
        var end = SalonTime.StartOfDay(SalonTime.ParseDay(to).AddDays(1), zone);

        return db.Sessions
            .AsNoTracking()
            .Include(s => s.Items).ThenInclude(i => i.Service)
            .Include(s => s.Employee)
            .Where(s => s.Status == SessionStatus.Completed && s.StartedAt >= start && s.StartedAt < end)
            .AsSplitQuery()
            .ToListAsync();
    }

    private static int Total(Session s) => s.Items.Sum(i => i.LineTotalCents);

    private static double DurationMs(Session s) =>
        s.FinishedAt is { } finished ? (finished - s.StartedAt).TotalMilliseconds : 0;

    private static int AverageMinutes(double totalMs, int count) =>
        count == 0 ? 0 : (int)Math.Round(totalMs / count / 60_000, MidpointRounding.AwayFromZero);

    private static async Task<object> Overview(string? from, string? to, AppDbContext db, AppSettings settings)
    {
        var sessions = await CompletedIn(db, settings, from, to);

        var byEmployee = sessions
            .GroupBy(s => s.EmployeeId)
            .Select(g => new
            {
                id = g.Key,
                name = g.First().Employee.Name,
                revenueCents = g.Sum(Total),
                sessionCount = g.Count(),
                avgDurationMinutes = AverageMinutes(g.Sum(DurationMs), g.Count()),
            })
            .OrderByDescending(e => e.revenueCents)
            .ToList();

        var byService = sessions
            .SelectMany(s => s.Items)
            .GroupBy(i => i.ServiceId)
            .Select(g => new
            {
                id = g.Key,
                nameTr = g.First().Service.NameTr,
                nameDe = g.First().Service.NameDe,
                count = g.Sum(i => i.Quantity),
                revenueCents = g.Sum(i => i.LineTotalCents),
            })
            .OrderByDescending(s => s.revenueCents)
            .ToList();

        return new
        {
            revenueCents = sessions.Sum(Total),
            sessionCount = sessions.Count,
            avgDurationMinutes = AverageMinutes(sessions.Sum(DurationMs), sessions.Count),
            byEmployee,
            byService,
        };
    }

    private static async Task<object> Timeseries(
        string? from, string? to, string? granularity, AppDbContext db, AppSettings settings)
    {
        var zone = settings.SalonTimeZone;
        Func<DateTime, string> bucketOf = (granularity ?? "day") switch
        {
            "day" => utc => SalonTime.Format(SalonTime.DayOf(utc, zone)),
            "week" => utc => SalonTime.Format(SalonTime.WeekOf(utc, zone)),
            "month" => utc => SalonTime.MonthOf(utc, zone),
            _ => throw ApiException.Validation(),
        };

        var sessions = await CompletedIn(db, settings, from, to);

        var rows = sessions
            .GroupBy(s => bucketOf(s.StartedAt))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(bucket => new
            {
                bucket = bucket.Key,
                employees = bucket
                    .GroupBy(s => s.EmployeeId)
                    .Select(e => new { id = e.Key, revenueCents = e.Sum(Total), sessionCount = e.Count() }),
                revenueCents = bucket.Sum(Total),
                sessionCount = bucket.Count(),
            });

        return new
        {
            granularity = granularity ?? "day",
            employees = sessions
                .GroupBy(s => s.EmployeeId)
                .Select(g => new { id = g.Key, name = g.First().Employee.Name }),
            rows,
        };
    }
}
