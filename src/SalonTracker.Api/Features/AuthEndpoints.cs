using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Contracts;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

public sealed record LoginRequest(string Username, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record LocaleRequest(string Locale);

public sealed class LoginValidator : AbstractValidator<LoginRequest>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty();
        RuleFor(x => x.Password).NotEmpty();
    }
}

public sealed class ChangePasswordValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty();
        RuleFor(x => x.NewPassword).Length(Passwords.MinLength, Passwords.MaxLength);
    }
}

public sealed class LocaleValidator : AbstractValidator<LocaleRequest>
{
    public LocaleValidator() => RuleFor(x => x.Locale).Must(Locales.IsSupported);
}

public static class Locales
{
    public static readonly string[] Supported = ["tr", "de", "en"];

    public static bool IsSupported(string? locale) => locale is not null && Supported.Contains(locale);
}

public static class AuthEndpoints
{
    public const string LoginRateLimit = "login";
    public const string PasswordRateLimit = "password";

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapPost("/login", Login).RequireRateLimiting(LoginRateLimit).Validate<LoginRequest>();
        auth.MapPost("/logout", (HttpResponse response) =>
        {
            TokenService.SignOut(response);
            return new { ok = true };
        });

        var me = auth.MapGroup("/me").RequireAuthorization();
        me.MapGet("", (HttpContext context) => new { user = UserDto.From(context.CurrentUser()) });
        me.MapPatch("/password", ChangePassword)
            .RequireRateLimiting(PasswordRateLimit)
            .Validate<ChangePasswordRequest>();
        me.MapPatch("/locale", SetLocale).Validate<LocaleRequest>();
    }

    private static async Task<IResult> Login(
        LoginRequest body, AppDbContext db, TokenService tokens, HttpResponse response)
    {
        var username = body.Username.Trim().ToLowerInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Username == username);

        // the same answer for an unknown user and a wrong password
        if (user is null || !user.Active || user.DeletedAt is not null || !Passwords.Verify(body.Password, user.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        tokens.SignIn(response, user.Id);
        return Results.Ok(new { user = UserDto.From(user) });
    }

    private static async Task<object> ChangePassword(ChangePasswordRequest body, HttpContext context, AppDbContext db)
    {
        var user = context.CurrentUser();
        if (!Passwords.Verify(body.CurrentPassword, user.PasswordHash))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "wrong_password");
        }

        user.PasswordHash = Passwords.Hash(body.NewPassword);
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    private static async Task<object> SetLocale(LocaleRequest body, HttpContext context, AppDbContext db)
    {
        var user = context.CurrentUser();
        user.Locale = body.Locale;
        await db.SaveChangesAsync();
        return new { user = UserDto.From(user) };
    }
}
