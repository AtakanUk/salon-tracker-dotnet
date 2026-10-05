using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Data;
using SalonTracker.Api.Features;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Cli;

/// <summary>
/// Creates the admin account, sample employees and a price list. Safe to run again: it
/// never touches an account that exists. Passwords are generated and printed once -
/// there are no default passwords, because in public mode the login page is on the
/// internet and a shipped "admin123" would be found within hours.
/// </summary>
public sealed class Seeder(AppDbContext db, AppSettings settings, TimeProvider time)
{
    private static readonly (string Name, string Username, Role Role)[] Accounts =
    [
        ("Patron", "admin", Role.Admin),
        ("Ali", "ali", Role.Employee),
        ("Mehmet", "mehmet", Role.Employee),
        ("Deniz", "deniz", Role.Employee),
    ];

    private static readonly (string Tr, string De, int PriceCents)[] PriceList =
    [
        ("Saç kesimi", "Haarschnitt", 2000),
        ("Sakal tıraşı", "Bartschnitt", 1200),
        ("Saç yıkama", "Haarwäsche", 500),
        ("Çocuk tıraşı", "Kinderhaarschnitt", 1500),
        ("Saç boyama", "Haare färben", 3500),
        ("Fön / Şekillendirme", "Föhnen / Styling", 800),
        ("Ağda", "Waxing", 800),
    ];

    public async Task<IReadOnlyList<(string Username, string Password)>> SeedAsync(string locale)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var created = new List<(string, string)>();

        foreach (var (name, username, role) in Accounts)
        {
            if (await db.Users.AnyAsync(u => u.Username == username)) continue;
            var password = role == Role.Admin ? Passwords.GenerateStrong() : Passwords.Generate();
            db.Users.Add(new User
            {
                Name = name,
                Username = username,
                PasswordHash = Passwords.Hash(password),
                Role = role,
                Locale = locale,
                CreatedAt = now,
            });
            created.Add((username, password));
        }

        for (var i = 0; i < PriceList.Length; i++)
        {
            var (tr, de, price) = PriceList[i];
            if (await db.Services.AnyAsync(s => s.NameTr == tr)) continue;
            db.Services.Add(new Service { NameTr = tr, NameDe = de, PriceCents = price, SortOrder = i, CreatedAt = now });
        }

        await db.SaveChangesAsync();
        return created;
    }

    /// <summary>
    /// About 60 days of made-up sessions for trying out the dashboard; only when there are
    /// none yet. Haircuts older than 30 days cost 2 EUR less - the price snapshots at work.
    /// </summary>
    public async Task<int> SeedDemoAsync()
    {
        if (await db.Sessions.AnyAsync()) return 0;

        var employees = await db.Users.Where(u => u.Role == Role.Employee).ToListAsync();
        var services = await db.Services.ToDictionaryAsync(s => s.NameTr);
        // chance that a customer gets the service, and roughly how long it takes
        (Service Service, double Chance, int Minutes)[] menu =
        [
            (services["Saç kesimi"], 0.85, 25),
            (services["Sakal tıraşı"], 0.45, 10),
            (services["Saç yıkama"], 0.2, 5),
            (services["Çocuk tıraşı"], 0.1, 20),
            (services["Saç boyama"], 0.06, 40),
            (services["Fön / Şekillendirme"], 0.15, 8),
        ];
        // different workloads, so the charts look like a real shop
        var workload = new Dictionary<string, int> { ["ali"] = 9, ["mehmet"] = 7, ["deniz"] = 5 };
        var zone = settings.SalonTimeZone;
        var now = time.GetUtcNow().UtcDateTime;
        var random = Random.Shared;
        var created = 0;

        for (var daysAgo = 60; daysAgo >= 0; daysAgo--)
        {
            var day = SalonTime.DayOf(now.AddDays(-daysAgo), zone);
            if (day.DayOfWeek == DayOfWeek.Sunday) continue; // closed on Sundays
            var opening = SalonTime.StartOfDay(day, zone);

            foreach (var employee in employees)
            {
                var customers = Math.Max(1, (int)Math.Round(workload.GetValueOrDefault(employee.Username, 6) + (random.NextDouble() - 0.5) * 4));
                for (var c = 0; c < customers; c++)
                {
                    var startedAt = opening.AddHours(9 + random.NextDouble() * 9.5); // 09:00 - 18:30
                    if (startedAt > now.AddHours(-1)) continue;

                    var picked = menu.Where(m => random.NextDouble() < m.Chance).ToList();
                    if (picked.Count == 0) picked.Add(menu[0]);
                    var minutes = picked.Sum(m => m.Minutes) + random.Next(0, 9);
                    var oldPrices = daysAgo > 30;

                    var session = new Session
                    {
                        EmployeeId = employee.Id,
                        StartedAt = startedAt,
                        FinishedAt = startedAt.AddMinutes(minutes),
                        Status = SessionStatus.Completed,
                    };
                    foreach (var (service, _, _) in picked)
                    {
                        var haircutBeforeRaise = oldPrices && service.NameTr == "Saç kesimi";
                        session.Items.Add(new SessionItem
                        {
                            Service = service,
                            Quantity = 1,
                            PriceCentsSnapshot = haircutBeforeRaise ? service.PriceCents - 200 : service.PriceCents,
                        });
                    }
                    db.Sessions.Add(session);
                    created++;
                }
            }
        }

        await db.SaveChangesAsync();
        return created;
    }

    public static string LocaleFromEnvironment() =>
        Environment.GetEnvironmentVariable("SEED_LOCALE") is { } locale && Locales.IsSupported(locale) ? locale : "en";
}
