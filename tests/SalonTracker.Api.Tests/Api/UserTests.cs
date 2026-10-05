using System.Net;
using SalonTracker.Api.Data;
using SalonTracker.Api.Tests.Infrastructure;

namespace SalonTracker.Api.Tests.Api;

public sealed class UserTests(ApiFixture fixture) : ApiTest(fixture)
{
    [Fact]
    public async Task Deleting_an_employee_keeps_their_name_and_revenue_in_past_statistics()
    {
        var owner = await CreateUser(role: Role.Admin);
        var deniz = await CreateUser(name: "Deniz", username: "deniz");
        var haircut = await CreateService(priceCents: 2000);
        await RecordSession(deniz.Cookie, Item(haircut.Id));

        async Task<string> Overview() =>
            await (await Get($"/api/stats/overview?from={Today}&to={Today}", owner.Cookie)).Content.ReadAsStringAsync();
        var before = await Overview();

        Assert.Equal(HttpStatusCode.OK, (await Delete($"/api/users/{deniz.User.Id}", owner.Cookie)).StatusCode);

        Assert.Equal(before, await Overview());
        Assert.Contains("\"name\":\"Deniz\"", before);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/auth/me", deniz.Cookie)).StatusCode);
    }

    [Fact]
    public async Task A_deleted_username_is_free_again_and_restoring_will_not_take_it_back()
    {
        var owner = await CreateUser(role: Role.Admin);
        var old = await CreateUser(name: "Deniz", username: "deniz");
        await Delete($"/api/users/{old.User.Id}", owner.Cookie);

        var fresh = await Post("/api/users", owner.Cookie, new { name = "Deniz Y.", username = "Deniz", password = "koltuk-1234" });
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
        Assert.Equal("deniz", (await Json(fresh)).GetProperty("user").GetProperty("username").GetString());

        var restore = await Post($"/api/users/{old.User.Id}/restore", owner.Cookie);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        Assert.Equal("username_taken", await ErrorCode(restore));
    }

    [Fact]
    public async Task Restoring_brings_back_the_old_username_and_password()
    {
        var owner = await CreateUser(role: Role.Admin);
        var old = await CreateUser(username: "deniz", password: "koltuk-1234");
        var deleted = await Json(await Delete($"/api/users/{old.User.Id}", owner.Cookie));
        // the "#id" suffix is internal; the list shows the plain name
        Assert.Equal("deniz", deleted.GetProperty("user").GetProperty("username").GetString());

        var restored = await Json(await Post($"/api/users/{old.User.Id}/restore", owner.Cookie));
        Assert.Equal("deniz", restored.GetProperty("user").GetProperty("username").GetString());
        Assert.False(restored.GetProperty("user").GetProperty("deleted").GetBoolean());

        var login = await Post("/api/auth/login", body: new { username = "deniz", password = "koltuk-1234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task The_owner_cannot_lock_themselves_out_or_delete_someone_with_a_customer_in_the_chair()
    {
        var owner = await CreateUser(role: Role.Admin);
        var busy = await CreateUser();
        await Post("/api/sessions/start", busy.Cookie);

        Assert.Equal("cannot_modify_self", await ErrorCode(await Delete($"/api/users/{owner.User.Id}", owner.Cookie)));
        Assert.Equal("cannot_modify_self", await ErrorCode(await Patch($"/api/users/{owner.User.Id}", owner.Cookie, new { role = "EMPLOYEE" })));
        Assert.Equal("employee_busy", await ErrorCode(await Delete($"/api/users/{busy.User.Id}", owner.Cookie)));
    }

    [Fact]
    public async Task A_permanent_delete_needs_a_normal_delete_first_and_shows_what_it_destroys()
    {
        var owner = await CreateUser(role: Role.Admin);
        var leaver = await CreateUser();
        var haircut = await CreateService(priceCents: 2000);
        await RecordSession(leaver.Cookie, Item(haircut.Id, quantity: 2));

        var tooEarly = await Delete($"/api/users/{leaver.User.Id}/permanent", owner.Cookie);
        Assert.Equal("not_deleted_yet", await ErrorCode(tooEarly));

        await Delete($"/api/users/{leaver.User.Id}", owner.Cookie);
        var impact = await Json(await Get($"/api/users/{leaver.User.Id}/impact", owner.Cookie));
        Assert.Equal(1, impact.GetProperty("sessionCount").GetInt32());
        Assert.Equal(4000, impact.GetProperty("revenueCents").GetInt32());

        var purge = await Json(await Delete($"/api/users/{leaver.User.Id}/permanent", owner.Cookie));
        Assert.Equal(1, purge.GetProperty("deletedSessions").GetInt32());
        Assert.Equal(0, await WithDb(db => Task.FromResult(db.Sessions.Count())));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ali veli")]
    [InlineData("ali#2")]
    [InlineData("ayşe")]
    public async Task Usernames_are_ascii_and_cannot_contain_the_deleted_marker(string username)
    {
        var owner = await CreateUser(role: Role.Admin);

        var response = await Post("/api/users", owner.Cookie, new { name = "X", username, password = "koltuk-1234" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
