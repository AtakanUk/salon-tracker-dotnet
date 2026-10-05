namespace SalonTracker.Api.Data;

public enum Role
{
    Admin,
    Employee,
}

public enum SessionStatus
{
    Active,
    Completed,
    Cancelled,
}

public class User
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Lowercase and unique. A deleted account becomes <c>name#id</c>, which frees the name.</summary>
    public required string Username { get; set; }

    public required string PasswordHash { get; set; }
    public Role Role { get; set; } = Role.Employee;
    public string Locale { get; set; } = "tr";
    public bool Active { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    /// <summary>Soft delete: the row stays so past sessions keep showing this person's name.</summary>
    public DateTime? DeletedAt { get; set; }

    public List<Session> Sessions { get; } = [];

    /// <summary>The <c>#id</c> suffix of a deleted account is internal and never shown.</summary>
    public string DisplayUsername => DeletedAt is null ? Username : Username.Split('#')[0];
}

public class Service
{
    public int Id { get; set; }
    public required string NameTr { get; set; }
    public required string NameDe { get; set; }

    /// <summary>The current list price. Records keep their own copy, see <see cref="SessionItem.PriceCentsSnapshot"/>.</summary>
    public int PriceCents { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Services are deactivated, never deleted, so old records keep a valid reference.</summary>
    public bool Active { get; set; } = true;

    /// <summary>The custom service: its amount is typed in for each record and <see cref="PriceCents"/> is unused.</summary>
    public bool Custom { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class Session
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public User Employee { get; set; } = null!;

    /// <summary>Always the server's clock: tablet clocks drift and can be changed.</summary>
    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public DateTime? EditedAt { get; set; }
    public List<SessionItem> Items { get; } = [];
}

public class SessionItem
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public Session Session { get; set; } = null!;
    public int ServiceId { get; set; }
    public Service Service { get; set; } = null!;

    /// <summary>The price at the moment the record was saved; later price changes never touch it.</summary>
    public int PriceCentsSnapshot { get; set; }

    public int Quantity { get; set; } = 1;

    /// <summary>What the custom job was, e.g. "bridal updo". Only custom services carry one.</summary>
    public string? Note { get; set; }

    public int LineTotalCents => PriceCentsSnapshot * Quantity;
}
