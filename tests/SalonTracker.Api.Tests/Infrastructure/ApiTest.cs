using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Tests.Infrastructure;

public sealed record TestUser(User User, string Cookie);

/// <summary>Base class for tests that talk to the API over HTTP, the way the web app does.</summary>
[Collection(ApiCollection.Name)]
public abstract class ApiTest(ApiFixture fixture) : IAsyncLifetime
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private int _counter;

    protected ApiFixture Fixture { get; } = fixture;

    protected HttpClient Client { get; } =
        fixture.Factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    /// <summary>Today in the salon's calendar, as the statistics endpoints expect it.</summary>
    protected string Today => SalonTime.Format(SalonTime.DayOf(Fixture.Clock.GetUtcNow().UtcDateTime, Berlin));

    public ValueTask InitializeAsync() => new(Fixture.ResetAsync());

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    protected async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    protected Task WithDb(Func<AppDbContext, Task> action) =>
        WithDb(async db =>
        {
            await action(db);
            return true;
        });

    /// <summary>A user straight in the database, plus a cookie that signs them in.</summary>
    protected async Task<TestUser> CreateUser(
        string? name = null, string? username = null, Role role = Role.Employee, string password = "correct-horse")
    {
        var n = Interlocked.Increment(ref _counter);
        var user = await WithDb(async db =>
        {
            var entity = new User
            {
                Name = name ?? $"User {n}",
                Username = username ?? $"user{n}",
                Role = role,
                // low work factor: these hashes only have to survive the test run
                PasswordHash = Passwords.Hash(password, workFactor: 4),
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(entity);
            await db.SaveChangesAsync();
            return entity;
        });
        var token = Fixture.Factory.Services.GetRequiredService<TokenService>().Issue(user.Id);
        return new TestUser(user, $"{TokenService.CookieName}={token}");
    }

    protected Task<Service> CreateService(string name = "Haircut", int priceCents = 2000, bool custom = false, bool active = true) =>
        WithDb(async db =>
        {
            var service = new Service
            {
                NameTr = name,
                NameDe = name,
                PriceCents = priceCents,
                Custom = custom,
                Active = active,
                CreatedAt = DateTime.UtcNow,
            };
            db.Services.Add(service);
            await db.SaveChangesAsync();
            return service;
        });

    protected Task<HttpResponseMessage> Send(HttpMethod method, string url, string? cookie = null, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (body is not null) request.Content = JsonContent.Create(body);
        return Client.SendAsync(request);
    }

    protected Task<HttpResponseMessage> Get(string url, string? cookie = null) => Send(HttpMethod.Get, url, cookie);

    protected Task<HttpResponseMessage> Post(string url, string? cookie = null, object? body = null) =>
        Send(HttpMethod.Post, url, cookie, body);

    protected Task<HttpResponseMessage> Patch(string url, string? cookie = null, object? body = null) =>
        Send(HttpMethod.Patch, url, cookie, body);

    protected Task<HttpResponseMessage> Delete(string url, string? cookie = null) => Send(HttpMethod.Delete, url, cookie);

    protected static async Task<JsonElement> Json(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>();

    protected static async Task<string?> ErrorCode(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("error").GetString();

    /// <summary>Start and finish a session the way the tablet does; returns the finished session.</summary>
    protected async Task<JsonElement> RecordSession(string cookie, params object[] items)
    {
        var start = await Post("/api/sessions/start", cookie);
        Assert.Equal(System.Net.HttpStatusCode.Created, start.StatusCode);
        var id = (await Json(start)).GetProperty("session").GetProperty("id").GetInt32();

        var finish = await Post($"/api/sessions/{id}/finish", cookie, new { items });
        Assert.True(finish.IsSuccessStatusCode, await finish.Content.ReadAsStringAsync());
        return (await Json(finish)).GetProperty("session");
    }

    protected static object Item(int serviceId, int quantity = 1, int? priceCents = null, string? note = null) =>
        new { serviceId, quantity, priceCents, note };
}
