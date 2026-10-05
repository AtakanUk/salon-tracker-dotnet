using System.Text.Json.Serialization;
using SalonTracker.Api.Data;

namespace SalonTracker.Api.Contracts;

// The response shapes the web app is built against. Field names, nullability and
// which fields are left out are part of the contract.

public sealed record UserDto(
    int Id,
    string Name,
    string Username,
    Role Role,
    string Locale,
    bool Active,
    bool Deleted,
    DateTime CreatedAt)
{
    public static UserDto From(User u) =>
        new(u.Id, u.Name, u.DisplayUsername, u.Role, u.Locale, u.Active, u.DeletedAt is not null, u.CreatedAt);
}

public sealed record ServiceDto(
    int Id,
    string NameTr,
    string NameDe,
    int PriceCents,
    int SortOrder,
    bool Active,
    bool Custom,
    DateTime CreatedAt)
{
    public static ServiceDto From(Service s) =>
        new(s.Id, s.NameTr, s.NameDe, s.PriceCents, s.SortOrder, s.Active, s.Custom, s.CreatedAt);
}

public sealed record SessionItemDto(
    int Id,
    int ServiceId,
    string NameTr,
    string NameDe,
    int PriceCents,
    int Quantity,
    bool Custom,
    string? Note);

public sealed record EmployeeRef(int Id, string Name);

public sealed record SessionDto(
    int Id,
    int EmployeeId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] EmployeeRef? Employee,
    DateTime StartedAt,
    DateTime? FinishedAt,
    SessionStatus Status,
    DateTime? EditedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SessionItemDto>? Items,
    int TotalCents,
    int? DurationMinutes)
{
    /// <summary>
    /// <paramref name="withItems"/> and <paramref name="withEmployee"/> say whether the caller
    /// loaded those relations; the fields are left out otherwise, like in the Node version.
    /// </summary>
    public static SessionDto From(Session s, bool withItems = true, bool withEmployee = false)
    {
        var items = withItems
            ? s.Items
                .OrderBy(i => i.Id)
                .Select(i => new SessionItemDto(
                    i.Id,
                    i.ServiceId,
                    i.Service.NameTr,
                    i.Service.NameDe,
                    i.PriceCentsSnapshot,
                    i.Quantity,
                    // the picker has to know it may edit this amount when correcting a record
                    i.Service.Custom,
                    i.Note))
                .ToList()
            : null;

        return new SessionDto(
            s.Id,
            s.EmployeeId,
            withEmployee ? new EmployeeRef(s.Employee.Id, s.Employee.Name) : null,
            s.StartedAt,
            s.FinishedAt,
            s.Status,
            s.EditedAt,
            items,
            items?.Sum(i => i.PriceCents * i.Quantity) ?? 0,
            s.FinishedAt is { } finished ? RoundMinutes(finished - s.StartedAt) : null);
    }

    private static int RoundMinutes(TimeSpan span) =>
        (int)Math.Round(span.TotalMinutes, MidpointRounding.AwayFromZero);
}
