using System.Security.Cryptography;

namespace SalonTracker.Api.Infrastructure;

public static class Passwords
{
    /// <summary>Shortest password accepted anywhere (in public mode the login page is on the internet).</summary>
    public const int MinLength = 8;

    public const int MaxLength = 100;

    // Salon words, ASCII only: these get read aloud and typed on a tablet keyboard,
    // so no Turkish characters and no easily confused letters.
    private static readonly string[] Words =
        ["makas", "tarak", "ayna", "havlu", "firca", "koltuk", "salon", "usta", "kesim", "sakal", "boya", "perma"];

    /// <summary>One-off password for employees and resets, e.g. "makas-4821".</summary>
    public static string Generate() =>
        $"{Words[RandomNumberGenerator.GetInt32(Words.Length)]}-{RandomNumberGenerator.GetInt32(1000, 10000)}";

    /// <summary>
    /// For admin accounts. Deliberately not readable aloud: an admin account can be
    /// reached from the internet in public mode, and it is typed on a real keyboard.
    /// </summary>
    public static string GenerateStrong() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('+', '-').Replace('/', '_');

    /// <summary>bcrypt, cost 10 - the same hashes as the Node version, so accounts move between them.</summary>
    public static string Hash(string password, int workFactor = 10) =>
        BCrypt.Net.BCrypt.HashPassword(password, workFactor);

    public static bool Verify(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            return false;
        }
    }
}
