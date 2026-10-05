using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Backup;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Cli;

/// <summary>
/// Server-side tools, run as <c>dotnet SalonTracker.Api.dll &lt;command&gt;</c>. The
/// <c>friseur</c> admin menu calls them inside the container.
/// </summary>
public static class Commands
{
    private const string Usage = """
        Usage: SalonTracker.Api [command]

          (no command)                 start the web server
          seed [--demo]                admin account, sample employees, price list (+ 60 days of demo data)
          backup                       take a backup now
          test-mail                    send a test mail to ALERT_TO
          reset-password [user] [pw]   reset a password; without arguments, pick from a list
          import-json <file.json.gz>   add the records of a backup that are missing here
        """;

    private static readonly string[] Names = ["seed", "backup", "test-mail", "reset-password", "import-json", "help", "--help"];

    public static bool IsCommand(string[] args) => args.Length > 0 && Names.Contains(args[0]);

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;

        switch (args[0])
        {
            case "seed":
                return await Seed(provider, demo: args.Contains("--demo") || Environment.GetEnvironmentVariable("SEED_DEMO") == "1");
            case "backup":
                var result = await provider.GetRequiredService<BackupService>().RunAsync();
                Console.WriteLine($"Backup {(result.Ok ? "done" : "FAILED")}: {string.Join(", ", result.Files)}");
                if (!result.Dump.Ok) Console.WriteLine($"  pg_dump: {result.Dump.Error}");
                return result.Ok ? 0 : 1;
            case "test-mail":
                return await TestMail(provider.GetRequiredService<Mailer>());
            case "reset-password":
                return await PasswordReset.RunAsync(provider.GetRequiredService<AppDbContext>(), args.Skip(1).ToArray());
            case "import-json":
                return await ImportJson(provider.GetRequiredService<JsonBackup>(), args.ElementAtOrDefault(1));
            default:
                Console.WriteLine(Usage);
                return 0;
        }
    }

    private static async Task<int> Seed(IServiceProvider provider, bool demo)
    {
        var seeder = provider.GetRequiredService<Seeder>();
        var created = await seeder.SeedAsync(Seeder.LocaleFromEnvironment());
        Console.WriteLine("Seeded users and services.");

        if (created.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
            Console.WriteLine("║  NEW ACCOUNTS — these passwords are NOT shown again       ║");
            Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
            foreach (var (username, password) in created) Console.WriteLine($"   {username,-10} {password}");
            Console.WriteLine();
            Console.WriteLine("   Write them down now. Lost one? friseur password");
            Console.WriteLine();
        }

        if (demo)
        {
            var sessions = await seeder.SeedDemoAsync();
            Console.WriteLine(sessions > 0 ? $"Created {sessions} demo sessions." : "Sessions already exist, skipping demo data.");
        }
        return 0;
    }

    private static async Task<int> TestMail(Mailer mailer)
    {
        if (!mailer.Configured)
        {
            Console.Error.WriteLine("Email is not set up: fill in SMTP_HOST/SMTP_USER/SMTP_PASS/ALERT_TO in .env.");
            return 1;
        }
        var result = await mailer.SendAsync(
            "Friseur – test mail", "This is a test mail. Alerts and backup mails will arrive at this address.");
        Console.WriteLine(result.Sent ? "Mail sent ✓" : $"Mail could not be sent: {result.Error}");
        return result.Sent ? 0 : 1;
    }

    private static async Task<int> ImportJson(JsonBackup json, string? path)
    {
        if (path is null)
        {
            Console.Error.WriteLine("Usage: import-json <friseur-YYYY-MM-DD.json.gz>");
            return 1;
        }
        try
        {
            var file = JsonBackup.Parse(await File.ReadAllBytesAsync(path));
            Console.WriteLine($"Backup taken: {file.ExportedAt:O}");
            var summary = await json.ImportAsync(file);
            Console.WriteLine(
                $"Done: {summary.Added.Total} missing rows restored (users {summary.Added.Users}, " +
                $"services {summary.Added.Services}, sessions {summary.Added.Sessions}, items {summary.Added.Items}).");
            return 0;
        }
        catch (ApiException e)
        {
            Console.Error.WriteLine($"Cannot read {path}: {e.Code}");
            return 1;
        }
    }
}

/// <summary>
/// The way back in when nobody can sign in. Needs server access only. A deactivated or
/// deleted account is reopened, otherwise a locked-out single admin could never be recovered.
/// </summary>
internal static class PasswordReset
{
    public static async Task<int> RunAsync(AppDbContext db, string[] args)
    {
        var users = await db.Users
            .OrderBy(u => u.DeletedAt != null).ThenByDescending(u => u.Active).ThenBy(u => u.Name)
            .ToListAsync();
        if (users.Count == 0)
        {
            Console.Error.WriteLine("There are no users.");
            return 1;
        }

        var (user, password) = args.Length > 0 ? PickByName(users, args[0], args.ElementAtOrDefault(1)) : PickFromList(users);
        if (user is null) return 1;
        password ??= Passwords.Generate();
        if (password.Length < Passwords.MinLength)
        {
            Console.Error.WriteLine($"The password needs at least {Passwords.MinLength} characters.");
            return 1;
        }

        var blocked = !user.Active || user.DeletedAt is not null;
        var wantedName = user.DisplayUsername;
        if (blocked && user.DeletedAt is not null)
        {
            // hand the plain username back, unless a live account took it in the meantime
            var clash = await db.Users.AnyAsync(u => u.Username == wantedName && u.Id != user.Id);
            if (!clash) user.Username = wantedName;
            user.DeletedAt = null;
        }
        user.Active = true;
        user.PasswordHash = Passwords.Hash(password);
        await db.SaveChangesAsync();

        Console.WriteLine($"\n  Account  : {user.Name} ({user.Username})");
        Console.WriteLine($"  Password : {password}\n");
        if (blocked) Console.WriteLine("Note: the account was inactive/deleted and has been reopened.");
        if (user.Username != wantedName)
        {
            Console.WriteLine($"Note: \"{wantedName}\" is taken by someone else, so the login name stays \"{user.Username}\".");
        }
        Console.WriteLine("After signing in, the user can change their own password.\n");
        return 0;
    }

    private static (User?, string?) PickByName(List<User> users, string name, string? password)
    {
        // live usernames are unique, but several deleted accounts can share a display
        // name - never guess between them, that is how the wrong account gets reopened
        var matches = users.Where(u => string.Equals(u.DisplayUsername, name.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var live = matches.Where(u => u.DeletedAt is null).ToList();
        var candidates = live.Count > 0 ? live : matches;

        if (candidates.Count == 0) Console.Error.WriteLine($"No user called \"{name}\".");
        else if (candidates.Count > 1) Console.Error.WriteLine($"Several deleted accounts are called \"{name}\". Run without arguments to pick from the list.");
        return candidates.Count == 1 ? (candidates[0], password) : (null, null);
    }

    private static (User?, string?) PickFromList(List<User> users)
    {
        Console.WriteLine("\nUsers:");
        for (var i = 0; i < users.Count; i++)
        {
            var u = users[i];
            var state = u.DeletedAt is not null ? "  [DELETED]" : u.Active ? "" : "  [INACTIVE]";
            Console.WriteLine($"  {i + 1,2}) {u.DisplayUsername,-16} {u.Name,-20} {(u.Role == Role.Admin ? "Admin" : "Employee")}{state}");
        }

        Console.Write("\nWhose password should be reset? (number, Enter to cancel): ");
        var choice = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(choice))
        {
            Console.WriteLine("Cancelled.");
            return (null, null);
        }
        if (!int.TryParse(choice, out var n) || n < 1 || n > users.Count)
        {
            Console.Error.WriteLine("Invalid number.");
            return (null, null);
        }

        Console.Write("New password (Enter = generate one): ");
        var typed = Console.ReadLine()?.Trim();
        return (users[n - 1], string.IsNullOrEmpty(typed) ? null : typed);
    }
}
