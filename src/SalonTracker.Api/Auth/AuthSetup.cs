using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Auth;

public static class AuthSetup
{
    public const string AdminPolicy = "admin";
    private const string CurrentUserKey = "salon.current-user";

    public static IServiceCollection AddSalonAuth(this IServiceCollection services)
    {
        services.AddSingleton<TokenService>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<TokenService>((options, tokens) =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = tokens.ValidationParameters;
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        context.Token = context.Request.Cookies[TokenService.CookieName];
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = LoadUser,
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        await ApiErrors.Write(context.Response, StatusCodes.Status401Unauthorized, "unauthorized");
                    },
                    OnForbidden = context =>
                        ApiErrors.Write(context.Response, StatusCodes.Status403Forbidden, "forbidden"),
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireRole(nameof(Role.Admin)));
        return services;
    }

    /// <summary>
    /// A valid token is not enough: the account must still exist and be active. The role
    /// is read from the database on every request, so a demoted or deactivated account
    /// loses access immediately, not when its 60-day token expires.
    /// </summary>
    private static async Task LoadUser(TokenValidatedContext context)
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var subject = context.Principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var user = int.TryParse(subject, out var id) ? await db.Users.FindAsync(id) : null;
        if (user is null || !user.Active || user.DeletedAt is not null)
        {
            context.Fail("account unavailable");
            return;
        }

        context.HttpContext.Items[CurrentUserKey] = user;
        ((ClaimsIdentity)context.Principal!.Identity!).AddClaim(new Claim(ClaimTypes.Role, user.Role.ToString()));
    }

    /// <summary>The signed-in user, tracked by this request's <see cref="AppDbContext"/>.</summary>
    public static User CurrentUser(this HttpContext context) =>
        context.Items[CurrentUserKey] as User
        ?? throw new InvalidOperationException("Endpoint is missing RequireAuthorization()");

    public static bool IsAdmin(this User user) => user.Role == Role.Admin;
}
