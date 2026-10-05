using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Contracts;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

public sealed record CreateUserRequest(string Name, string Username, string Password, Role? Role, string? Locale);

public sealed record UpdateUserRequest(
    string? Name, string? Username, string? Password, Role? Role, string? Locale, bool? Active);

public static partial class Usernames
{
    /// <summary>
    /// ASCII only, no '#': a deleted account's username gets a "#id" suffix, so it can
    /// never clash with a live one and the name is free for a new account at once.
    /// </summary>
    public static bool IsValid(string? username) => username is { Length: >= 2 and <= 30 } && Pattern().IsMatch(username);

    public static string Normalize(string username) => username.ToLowerInvariant();

    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex Pattern();
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
        RuleFor(x => x.Username).Must(Usernames.IsValid);
        RuleFor(x => x.Password).Length(Passwords.MinLength, Passwords.MaxLength);
        RuleFor(x => x.Locale).Must(Locales.IsSupported).When(x => x.Locale is not null);
    }
}

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60).When(x => x.Name is not null);
        RuleFor(x => x.Username).Must(Usernames.IsValid).When(x => x.Username is not null);
        RuleFor(x => x.Password).Length(Passwords.MinLength, Passwords.MaxLength).When(x => x.Password is not null);
        RuleFor(x => x.Locale).Must(Locales.IsSupported).When(x => x.Locale is not null);
    }
}

/// <summary>Employee accounts, owner only.</summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/users").WithTags("Employees").RequireAuthorization(AuthSetup.AdminPolicy);

        users.MapGet("", List);
        users.MapPost("", Create).Validate<CreateUserRequest>();
        users.MapPatch("/{id:int}", Update).Validate<UpdateUserRequest>();
        users.MapDelete("/{id:int}", SoftDelete);
        users.MapGet("/{id:int}/impact", Impact);
        users.MapDelete("/{id:int}/permanent", PermanentDelete);
        users.MapPost("/{id:int}/restore", Restore);
    }

    /// <summary>Live accounts first (active before inactive), deleted ones last.</summary>
    private static async Task<object> List(AppDbContext db)
    {
        var list = await db.Users
            .OrderBy(u => u.DeletedAt != null)
            .ThenBy(u => u.DeletedAt)
            .ThenByDescending(u => u.Active)
            .ThenBy(u => u.Name)
            .ToListAsync();
        return new { users = list.Select(UserDto.From) };
    }

    private static async Task<IResult> Create(CreateUserRequest body, AppDbContext db, TimeProvider time)
    {
        var user = new User
        {
            Name = body.Name,
            Username = Usernames.Normalize(body.Username),
            PasswordHash = Passwords.Hash(body.Password),
            Role = body.Role ?? Role.Employee,
            Locale = body.Locale ?? "tr",
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Users.Add(user);
        await SaveUnique(db);
        return Results.Created($"/api/users/{user.Id}", new { user = UserDto.From(user) });
    }

    private static async Task<object> Update(int id, UpdateUserRequest body, HttpContext context, AppDbContext db)
    {
        // the owner cannot lock themselves out
        if (id == context.CurrentUser().Id && (body.Active == false || body.Role == Role.Employee))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "cannot_modify_self");
        }

        var user = await db.Users.FindAsync(id);
        if (user is null || user.DeletedAt is not null) throw ApiException.NotFound();
        if (body.Active == false && user.Active) await AssertNotBusy(db, id);

        if (body.Name is not null) user.Name = body.Name;
        if (body.Username is not null) user.Username = Usernames.Normalize(body.Username);
        if (body.Role is { } role) user.Role = role;
        if (body.Locale is not null) user.Locale = body.Locale;
        if (body.Active is { } active) user.Active = active;
        if (body.Password is not null) user.PasswordHash = Passwords.Hash(body.Password);

        await SaveUnique(db);
        return new { user = UserDto.From(user) };
    }

    /// <summary>
    /// Soft delete: the row survives so every past session keeps showing this person's
    /// name. The username gets a "#id" suffix so it can be reused right away.
    /// </summary>
    private static async Task<object> SoftDelete(int id, HttpContext context, AppDbContext db, TimeProvider time)
    {
        if (id == context.CurrentUser().Id) throw new ApiException(StatusCodes.Status400BadRequest, "cannot_modify_self");

        var user = await db.Users.FindAsync(id);
        if (user is null || user.DeletedAt is not null) throw ApiException.NotFound();
        await AssertNotBusy(db, id);

        user.DeletedAt = time.GetUtcNow().UtcDateTime;
        user.Active = false;
        // the password hash is kept on purpose: restoring must not require a reset
        user.Username = $"{user.Username}#{id}";
        await db.SaveChangesAsync();
        return new { user = UserDto.From(user) };
    }

    /// <summary>What a permanent delete would destroy - shown in the confirmation dialog.</summary>
    private static async Task<object> Impact(int id, AppDbContext db)
    {
        if (!await db.Users.AnyAsync(u => u.Id == id)) throw ApiException.NotFound();

        var sessions = await db.Sessions
            .Where(s => s.EmployeeId == id)
            .OrderBy(s => s.StartedAt)
            .Select(s => new
            {
                s.StartedAt,
                s.Status,
                Total = s.Items.Sum(i => i.PriceCentsSnapshot * i.Quantity),
            })
            .ToListAsync();

        return new
        {
            sessionCount = sessions.Count,
            revenueCents = sessions.Where(s => s.Status == SessionStatus.Completed).Sum(s => s.Total),
            firstAt = sessions.FirstOrDefault()?.StartedAt,
            lastAt = sessions.LastOrDefault()?.StartedAt,
        };
    }

    /// <summary>
    /// The row and every session it owns are gone for good. Only offered for accounts
    /// that are already soft-deleted, so destroying history always takes two steps.
    /// </summary>
    private static async Task<object> PermanentDelete(
        int id, HttpContext context, AppDbContext db, ILogger<AppDbContext> logger)
    {
        if (id == context.CurrentUser().Id) throw new ApiException(StatusCodes.Status400BadRequest, "cannot_modify_self");

        var user = await db.Users.FindAsync(id) ?? throw ApiException.NotFound();
        if (user.DeletedAt is null) throw new ApiException(StatusCodes.Status409Conflict, "not_deleted_yet");

        await using var transaction = await db.Database.BeginTransactionAsync();
        var items = await db.SessionItems.Where(i => i.Session.EmployeeId == id).ExecuteDeleteAsync();
        var sessions = await db.Sessions.Where(s => s.EmployeeId == id).ExecuteDeleteAsync();
        await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
        await transaction.CommitAsync();

        logger.LogWarning("User {Id} ({Name}) permanently deleted with {Sessions} sessions", id, user.Name, sessions);
        return new { deletedSessions = sessions, deletedItems = items };
    }

    private static async Task<object> Restore(int id, AppDbContext db)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null || user.DeletedAt is null) throw ApiException.NotFound();

        user.Username = user.DisplayUsername;
        user.DeletedAt = null;
        user.Active = true;
        // someone may have taken the old username in the meantime
        await SaveUnique(db);
        return new { user = UserDto.From(user) };
    }

    /// <summary>A dangling ACTIVE session could never be finished, so block the change while one is open.</summary>
    private static async Task AssertNotBusy(AppDbContext db, int employeeId)
    {
        if (await db.Sessions.AnyAsync(s => s.EmployeeId == employeeId && s.Status == SessionStatus.Active))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "employee_busy");
        }
    }

    private static async Task SaveUnique(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (AppDbContext.IsUniqueViolation(e))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "username_taken");
        }
    }
}
