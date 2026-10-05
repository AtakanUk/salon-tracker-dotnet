using System.Net;
using SalonTracker.Api.Data;
using SalonTracker.Api.Tests.Infrastructure;

namespace SalonTracker.Api.Tests.Api;

public sealed class StatsTests(ApiFixture fixture) : ApiTest(fixture)
{
    private Task Completed(int employeeId, int serviceId, string startedAt, int priceCents = 2000) =>
        WithDb(async db =>
        {
            var start = DateTime.Parse(startedAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
            var session = new Session
            {
                EmployeeId = employeeId,
                StartedAt = start,
                FinishedAt = start.AddMinutes(30),
                Status = SessionStatus.Completed,
            };
            session.Items.Add(new SessionItem { ServiceId = serviceId, Quantity = 1, PriceCentsSnapshot = priceCents });
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Days_are_cut_at_the_salons_midnight_not_at_UTC_midnight()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService();
        await Completed(ali.User.Id, haircut.Id, "2026-03-01T23:30:00Z"); // 00:30 on 2 March in Berlin
        await Completed(ali.User.Id, haircut.Id, "2026-03-01T10:00:00Z");

        var series = await Json(await Get("/api/stats/timeseries?from=2026-03-01&to=2026-03-02&granularity=day", owner.Cookie));

        var rows = series.GetProperty("rows").EnumerateArray()
            .Select(r => (r.GetProperty("bucket").GetString(), r.GetProperty("sessionCount").GetInt32()))
            .ToList();
        Assert.Equal([("2026-03-01", 1), ("2026-03-02", 1)], rows);
    }

    [Fact]
    public async Task Revenue_is_split_per_employee_and_per_service()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser(name: "Ali");
        var mehmet = await CreateUser(name: "Mehmet");
        var haircut = await CreateService("Haircut");
        var beard = await CreateService("Beard", priceCents: 1200);
        await Completed(ali.User.Id, haircut.Id, "2026-03-02T09:00:00Z", 2000);
        await Completed(ali.User.Id, beard.Id, "2026-03-02T10:00:00Z", 1200);
        await Completed(mehmet.User.Id, haircut.Id, "2026-03-02T11:00:00Z", 1800);

        var body = await Json(await Get("/api/stats/overview?from=2026-03-02&to=2026-03-02", owner.Cookie));

        Assert.Equal(5000, body.GetProperty("revenueCents").GetInt32());
        Assert.Equal(3, body.GetProperty("sessionCount").GetInt32());
        Assert.Equal(30, body.GetProperty("avgDurationMinutes").GetInt32());
        var employees = body.GetProperty("byEmployee").EnumerateArray()
            .Select(e => (e.GetProperty("name").GetString(), e.GetProperty("revenueCents").GetInt32()))
            .ToList();
        Assert.Equal([("Ali", 3200), ("Mehmet", 1800)], employees);
        var haircuts = body.GetProperty("byService").EnumerateArray().Single(s => s.GetProperty("nameDe").GetString() == "Haircut");
        Assert.Equal(2, haircuts.GetProperty("count").GetInt32());
        Assert.Equal(3800, haircuts.GetProperty("revenueCents").GetInt32());
    }

    [Fact]
    public async Task Weeks_and_months_are_bucketed_in_the_salons_calendar()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService();
        await Completed(ali.User.Id, haircut.Id, "2026-03-31T22:30:00Z"); // already 1 April in Berlin
        await Completed(ali.User.Id, haircut.Id, "2026-03-30T08:00:00Z"); // Monday

        var months = await Json(await Get("/api/stats/timeseries?from=2026-03-01&to=2026-04-30&granularity=month", owner.Cookie));
        var weeks = await Json(await Get("/api/stats/timeseries?from=2026-03-01&to=2026-04-30&granularity=week", owner.Cookie));

        Assert.Equal(["2026-03", "2026-04"], months.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("bucket").GetString()));
        Assert.Equal(["2026-03-30"], weeks.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("bucket").GetString()));
    }

    [Theory]
    [InlineData("/api/stats/overview?from=2026-3-1&to=2026-03-02")]
    [InlineData("/api/stats/overview?from=2026-03-01")]
    [InlineData("/api/stats/timeseries?from=2026-03-01&to=2026-03-02&granularity=year")]
    public async Task Malformed_ranges_are_rejected(string url)
    {
        var owner = await CreateUser(role: Role.Admin);

        var response = await Get(url, owner.Cookie);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation", await ErrorCode(response));
    }
}
