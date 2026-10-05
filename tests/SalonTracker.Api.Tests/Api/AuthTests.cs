using System.Net;
using SalonTracker.Api.Data;
using SalonTracker.Api.Tests.Infrastructure;

namespace SalonTracker.Api.Tests.Api;

public sealed class AuthTests(ApiFixture fixture) : ApiTest(fixture)
{
    private Task<HttpResponseMessage> Login(string username, string password, string? forwardedFor = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { username, password }),
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return Client.SendAsync(request);
    }

    [Fact]
    public async Task Login_sets_an_httpOnly_cookie_and_matches_usernames_as_typed_on_a_tablet()
    {
        await CreateUser(username: "ali", password: "makas-4821");

        var response = await Login("  Ali ", "makas-4821");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("token=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_user_get_the_same_answer()
    {
        await CreateUser(username: "ali", password: "makas-4821");

        var wrong = await Login("ali", "makas-0000");
        var unknown = await Login("nobody", "makas-4821");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_deactivated_account_is_locked_out_including_its_existing_cookie()
    {
        var ali = await CreateUser(username: "ali", password: "makas-4821");
        await WithDb(async db =>
        {
            (await db.Users.FindAsync(ali.User.Id))!.Active = false;
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await Login("ali", "makas-4821")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/api/auth/me", ali.Cookie)).StatusCode);
    }

    [Fact]
    public async Task Login_attempts_are_limited_by_the_address_the_proxy_saw_not_one_the_client_made_up()
    {
        await CreateUser(username: "ali", password: "makas-4821");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 21; i++)
        {
            // a client can put anything into X-Forwarded-For; the proxy appends the real address
            statuses.Add((await Login("ali", "wrong-password", $"10.0.0.{i}, 203.0.113.7")).StatusCode);
        }

        Assert.All(statuses.Take(20), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[20]);
    }

    [Fact]
    public async Task Changing_the_role_takes_effect_on_the_next_request_not_when_the_token_expires()
    {
        var boss = await CreateUser(role: Role.Admin);
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/users", boss.Cookie)).StatusCode);

        await WithDb(async db =>
        {
            (await db.Users.FindAsync(boss.User.Id))!.Role = Role.Employee;
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.Forbidden, (await Get("/api/users", boss.Cookie)).StatusCode);
    }

    [Theory]
    [InlineData("/api/users")]
    [InlineData("/api/system/status")]
    [InlineData("/api/sessions")]
    public async Task Employees_cannot_reach_the_owners_endpoints(string url)
    {
        var employee = await CreateUser();

        var response = await Get(url, employee.Cookie);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await ErrorCode(response));
    }

    [Fact]
    public async Task Everything_but_login_and_health_needs_a_session()
    {
        var board = await Get("/api/sessions/board");
        Assert.Equal(HttpStatusCode.Unauthorized, board.StatusCode);
        Assert.Equal("unauthorized", await ErrorCode(board));
        Assert.Equal(HttpStatusCode.OK, (await Get("/api/health")).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var response = await Get("/api/health");

        Assert.Contains("default-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Changing_your_password_needs_the_current_one()
    {
        var ali = await CreateUser(username: "ali", password: "makas-4821");

        var wrong = await Patch("/api/auth/me/password", ali.Cookie, new { currentPassword = "nope", newPassword = "tarak-1234" });
        Assert.Equal("wrong_password", await ErrorCode(wrong));

        var tooShort = await Patch("/api/auth/me/password", ali.Cookie, new { currentPassword = "makas-4821", newPassword = "short" });
        Assert.Equal("validation", await ErrorCode(tooShort));

        var ok = await Patch("/api/auth/me/password", ali.Cookie, new { currentPassword = "makas-4821", newPassword = "tarak-1234" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Login("ali", "tarak-1234")).StatusCode);
    }
}
