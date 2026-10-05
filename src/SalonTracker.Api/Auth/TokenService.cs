using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SalonTracker.Api.Configuration;

namespace SalonTracker.Api.Auth;

/// <summary>Issues the session JWT that lives in an httpOnly cookie.</summary>
public sealed class TokenService(AppSettings settings, TimeProvider time, IHostEnvironment environment)
{
    public const string CookieName = "token";

    /// <summary>Tablets stay signed in.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(60);

    // HS256 wants a 256-bit key; hashing lets JWT_SECRET be any length
    private readonly SymmetricSecurityKey _key = new(SHA256.HashData(Encoding.UTF8.GetBytes(settings.JwtSecret)));

    public TokenValidationParameters ValidationParameters => new()
    {
        ValidateIssuer = false,
        ValidateAudience = false,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = _key,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromMinutes(1),
    };

    public string Issue(int userId) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object> { [JwtRegisteredClaimNames.Sub] = userId.ToString() },
            Expires = time.GetUtcNow().UtcDateTime + Lifetime,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256),
        });

    public void SignIn(HttpResponse response, int userId) =>
        response.Cookies.Append(CookieName, Issue(userId), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = environment.IsProduction(),
            Path = "/",
            MaxAge = Lifetime,
        });

    public static void SignOut(HttpResponse response) =>
        response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });
}
