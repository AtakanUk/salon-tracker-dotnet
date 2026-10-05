using System.Net;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Data;
using SalonTracker.Api.Tests.Infrastructure;

namespace SalonTracker.Api.Tests.Api;

public sealed class SessionTests(ApiFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task An_employee_can_have_only_one_open_customer()
    {
        var ali = await CreateUser();

        Assert.Equal(HttpStatusCode.Created, (await Post("/api/sessions/start", ali.Cookie)).StatusCode);
        var second = await Post("/api/sessions/start", ali.Cookie);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("active_session_exists", await ErrorCode(second));
    }

    [Fact]
    public async Task The_database_itself_rejects_a_second_open_session()
    {
        var ali = await CreateUser();

        // straight past the API check, the way a double tap could race past it
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => WithDb(async db =>
        {
            db.Sessions.Add(new Session { EmployeeId = ali.User.Id, StartedAt = DateTime.UtcNow });
            db.Sessions.Add(new Session { EmployeeId = ali.User.Id, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }));
        Assert.True(AppDbContext.IsUniqueViolation(error, AppDbContext.OneActiveSessionIndex));

        // finished sessions do not count
        await WithDb(async db =>
        {
            db.Sessions.Add(new Session { EmployeeId = ali.User.Id, StartedAt = DateTime.UtcNow, Status = SessionStatus.Completed });
            db.Sessions.Add(new Session { EmployeeId = ali.User.Id, StartedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task The_start_time_comes_from_the_server_not_the_tablet()
    {
        var ali = await CreateUser();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var response = await Post("/api/sessions/start", ali.Cookie, new { startedAt = "2020-01-01T00:00:00Z" });

        var startedAt = (await Json(response)).GetProperty("session").GetProperty("startedAt").GetDateTime();
        Assert.True(startedAt >= before);
    }

    [Fact]
    public async Task A_record_keeps_its_price_when_the_price_list_changes()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);

        var old = await RecordSession(ali.Cookie, Item(haircut.Id));
        Assert.Equal(2000, old.GetProperty("totalCents").GetInt32());

        var update = await Patch($"/api/services/{haircut.Id}", owner.Cookie, new { priceCents = 2200 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var fresh = await RecordSession(ali.Cookie, Item(haircut.Id));
        Assert.Equal(2200, fresh.GetProperty("totalCents").GetInt32());

        var stats = await Json(await Get($"/api/stats/overview?from={Today}&to={Today}", owner.Cookie));
        Assert.Equal(2000 + 2200, stats.GetProperty("revenueCents").GetInt32());
    }

    [Fact]
    public async Task A_listed_service_is_never_priced_from_the_request()
    {
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);

        var session = await RecordSession(ali.Cookie, Item(haircut.Id, priceCents: 1, note: "my own price"));

        Assert.Equal(2000, session.GetProperty("totalCents").GetInt32());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, session.GetProperty("items")[0].GetProperty("note").ValueKind);
    }

    [Fact]
    public async Task The_custom_service_takes_its_amount_and_note_from_the_request()
    {
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);
        var custom = await CreateService("Custom", priceCents: 0, custom: true);

        var session = await RecordSession(ali.Cookie, Item(haircut.Id), Item(custom.Id, priceCents: 3550, note: "  bridal updo  "));

        Assert.Equal(5550, session.GetProperty("totalCents").GetInt32());
        var line = session.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("custom").GetBoolean());
        Assert.Equal(3550, line.GetProperty("priceCents").GetInt32());
        Assert.Equal("bridal updo", line.GetProperty("note").GetString());
    }

    [Fact]
    public async Task The_custom_service_needs_an_amount()
    {
        var ali = await CreateUser();
        var custom = await CreateService("Custom", priceCents: 0, custom: true);
        var id = (await Json(await Post("/api/sessions/start", ali.Cookie))).GetProperty("session").GetProperty("id").GetInt32();

        var response = await Post($"/api/sessions/{id}/finish", ali.Cookie, new { items = new[] { Item(custom.Id) } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("amount_required", await ErrorCode(response));
    }

    [Fact]
    public async Task A_deactivated_service_cannot_be_picked()
    {
        var ali = await CreateUser();
        var retired = await CreateService(active: false);
        var id = (await Json(await Post("/api/sessions/start", ali.Cookie))).GetProperty("session").GetProperty("id").GetInt32();

        var response = await Post($"/api/sessions/{id}/finish", ali.Cookie, new { items = new[] { Item(retired.Id) } });

        Assert.Equal("unknown_service", await ErrorCode(response));
    }

    [Fact]
    public async Task Invalid_items_are_rejected_before_anything_is_saved()
    {
        var ali = await CreateUser();
        var haircut = await CreateService();
        var id = (await Json(await Post("/api/sessions/start", ali.Cookie))).GetProperty("session").GetProperty("id").GetInt32();

        var tooMany = await Post($"/api/sessions/{id}/finish", ali.Cookie, new { items = new[] { Item(haircut.Id, quantity: 21) } });
        var none = await Post($"/api/sessions/{id}/finish", ali.Cookie, new { items = Array.Empty<object>() });

        Assert.Equal("validation", await ErrorCode(tooMany));
        Assert.Equal("validation", await ErrorCode(none));
    }

    [Fact]
    public async Task A_correction_keeps_old_snapshots_and_prices_new_items_from_todays_list()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);
        var beard = await CreateService("Beard", priceCents: 1200);

        var session = await RecordSession(ali.Cookie, Item(haircut.Id));
        await Patch($"/api/services/{haircut.Id}", owner.Cookie, new { priceCents = 2500 });

        var response = await Patch(
            $"/api/sessions/{session.GetProperty("id").GetInt32()}/items",
            ali.Cookie,
            new { items = new[] { Item(haircut.Id), Item(beard.Id) } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var corrected = (await Json(response)).GetProperty("session");
        Assert.Equal(2000 + 1200, corrected.GetProperty("totalCents").GetInt32());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, corrected.GetProperty("editedAt").ValueKind);
    }

    [Fact]
    public async Task The_correction_window_closes_after_30_minutes_for_employees_but_not_for_the_owner()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService();
        var id = (await RecordSession(ali.Cookie, Item(haircut.Id))).GetProperty("id").GetInt32();

        Fixture.Clock.Offset = TimeSpan.FromMinutes(31);
        var items = new { items = new[] { Item(haircut.Id, quantity: 2) } };

        var late = await Patch($"/api/sessions/{id}/items", ali.Cookie, items);
        Assert.Equal(HttpStatusCode.Forbidden, late.StatusCode);
        Assert.Equal("edit_window_expired", await ErrorCode(late));
        Assert.Equal(HttpStatusCode.Forbidden, (await Post($"/api/sessions/{id}/cancel", ali.Cookie)).StatusCode);

        var board = await Json(await Get("/api/sessions/board", ali.Cookie));
        Assert.False(board.GetProperty("lastCompletedEditable").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await Patch($"/api/sessions/{id}/items", owner.Cookie, items)).StatusCode);
    }

    [Fact]
    public async Task Employees_cannot_see_each_others_records()
    {
        var alice = await CreateUser();
        var bob = await CreateUser();
        var haircut = await CreateService();
        var id = (await RecordSession(alice.Cookie, Item(haircut.Id))).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NotFound, (await Post($"/api/sessions/{id}/cancel", bob.Cookie)).StatusCode);
    }

    [Fact]
    public async Task Cancelled_records_are_left_out_of_the_statistics()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);
        await RecordSession(ali.Cookie, Item(haircut.Id));
        var mistake = await RecordSession(ali.Cookie, Item(haircut.Id, quantity: 3));

        await Post($"/api/sessions/{mistake.GetProperty("id").GetInt32()}/cancel", ali.Cookie);

        var stats = await Json(await Get($"/api/stats/overview?from={Today}&to={Today}", owner.Cookie));
        Assert.Equal(2000, stats.GetProperty("revenueCents").GetInt32());
        Assert.Equal(1, stats.GetProperty("sessionCount").GetInt32());
    }

    [Fact]
    public async Task The_board_has_the_open_customer_and_todays_records_newest_first()
    {
        var ali = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);
        await RecordSession(ali.Cookie, Item(haircut.Id));
        Fixture.Clock.Offset = TimeSpan.FromMinutes(5);
        var second = await RecordSession(ali.Cookie, Item(haircut.Id, quantity: 2));
        await Post("/api/sessions/start", ali.Cookie);

        var board = await Json(await Get("/api/sessions/board", ali.Cookie));

        Assert.False(board.GetProperty("active").TryGetProperty("items", out _)); // not loaded, so not sent
        Assert.Equal(2, board.GetProperty("today").GetProperty("count").GetInt32());
        Assert.Equal(6000, board.GetProperty("today").GetProperty("revenueCents").GetInt32());
        Assert.Equal(second.GetProperty("id").GetInt32(), board.GetProperty("today").GetProperty("sessions")[0].GetProperty("id").GetInt32());
        Assert.True(board.GetProperty("lastCompletedEditable").GetBoolean());
    }

    [Fact]
    public async Task The_owner_can_add_a_missed_record_and_fix_its_times()
    {
        var owner = await CreateUser(role: Role.Admin);
        var ali = await CreateUser(name: "Ali");
        var haircut = await CreateService(priceCents: 2000);

        var created = await Post("/api/sessions", owner.Cookie, new
        {
            employeeId = ali.User.Id,
            startedAt = "2026-09-01T09:00:00Z",
            finishedAt = "2026-09-01T09:30:00Z",
            items = new[] { Item(haircut.Id) },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var session = (await Json(created)).GetProperty("session");
        Assert.Equal("Ali", session.GetProperty("employee").GetProperty("name").GetString());
        Assert.Equal(30, session.GetProperty("durationMinutes").GetInt32());

        var id = session.GetProperty("id").GetInt32();
        var backwards = await Patch($"/api/sessions/{id}", owner.Cookie, new { finishedAt = "2026-09-01T08:00:00Z" });
        Assert.Equal("end_before_start", await ErrorCode(backwards));

        var moved = await Patch($"/api/sessions/{id}", owner.Cookie, new { startedAt = "2026-09-01T09:10:00Z" });
        Assert.Equal(20, (await Json(moved)).GetProperty("session").GetProperty("durationMinutes").GetInt32());
    }
}
