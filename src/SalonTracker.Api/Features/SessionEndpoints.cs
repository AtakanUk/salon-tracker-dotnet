using System.Text.Json.Nodes;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Configuration;
using SalonTracker.Api.Contracts;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

/// <summary>One line of the service picker. Amount and note only count for the custom service.</summary>
public sealed record PickedItem(int ServiceId, int Quantity, int? PriceCents, string? Note);

public sealed record FinishRequest(IReadOnlyList<PickedItem> Items);

public sealed record CreateSessionRequest(
    int EmployeeId, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, IReadOnlyList<PickedItem> Items);

public sealed class PickedItemValidator : AbstractValidator<PickedItem>
{
    public PickedItemValidator()
    {
        RuleFor(x => x.ServiceId).GreaterThan(0);
        RuleFor(x => x.Quantity).InclusiveBetween(1, 20);
        RuleFor(x => x.PriceCents).InclusiveBetween(1, 1_000_000);
        RuleFor(x => x.Note).MaximumLength(60);
    }
}

public sealed class FinishValidator : AbstractValidator<FinishRequest>
{
    public FinishValidator()
    {
        RuleFor(x => x.Items).NotNull().Must(i => i.Count is >= 1 and <= 30);
        RuleForEach(x => x.Items).SetValidator(new PickedItemValidator());
    }
}

public sealed class CreateSessionValidator : AbstractValidator<CreateSessionRequest>
{
    public CreateSessionValidator()
    {
        RuleFor(x => x.EmployeeId).GreaterThan(0);
        RuleFor(x => x.Items).NotNull().Must(i => i.Count is >= 1 and <= 30);
        RuleForEach(x => x.Items).SetValidator(new PickedItemValidator());
    }
}

public static class SessionEndpoints
{
    /// <summary>How long an employee may correct or cancel a finished session.</summary>
    public static readonly TimeSpan EditWindow = TimeSpan.FromMinutes(30);

    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        var sessions = app.MapGroup("/api/sessions").WithTags("Sessions").RequireAuthorization();

        sessions.MapGet("/board", Board);
        sessions.MapPost("/start", Start);
        sessions.MapPost("/{id:int}/finish", Finish).Validate<FinishRequest>();
        sessions.MapPost("/{id:int}/cancel", Cancel);
        sessions.MapPatch("/{id:int}/items", CorrectItems).Validate<FinishRequest>();

        var admin = sessions.MapGroup("").RequireAuthorization(AuthSetup.AdminPolicy);
        admin.MapGet("", List);
        admin.MapPost("", Create).Validate<CreateSessionRequest>();
        admin.MapPatch("/{id:int}", Edit);
        admin.MapDelete("/{id:int}", Delete);
    }

    /// <summary>
    /// A line with its price locked in. A listed service is always priced from the
    /// database, never from the request - otherwise an employee could name their own
    /// price for a haircut. The custom service is the one exception: its amount and note
    /// come from whoever fills in the record. This is the only place that decides it.
    /// </summary>
    internal static SessionItem PricedLine(PickedItem picked, Service service, int listPriceCents)
    {
        if (!service.Custom)
        {
            return new SessionItem { Service = service, Quantity = picked.Quantity, PriceCentsSnapshot = listPriceCents };
        }

        if (picked.PriceCents is not { } amount) throw new ApiException(StatusCodes.Status400BadRequest, "amount_required");
        return new SessionItem
        {
            Service = service,
            Quantity = picked.Quantity,
            PriceCentsSnapshot = amount,
            Note = string.IsNullOrWhiteSpace(picked.Note) ? null : picked.Note.Trim(),
        };
    }

    private static IQueryable<Session> WithItems(this IQueryable<Session> query) =>
        query.Include(s => s.Items).ThenInclude(i => i.Service);

    private static bool WithinEditWindow(DateTime? finishedAt, DateTime now) =>
        finishedAt is { } finished && now - finished <= EditWindow;

    /// <summary>
    /// Everything the employee screens need in one call. The start/finish screen faces the
    /// customer and only uses <c>active</c>; the rest is for the employee's "my day" screen.
    /// </summary>
    private static async Task<object> Board(HttpContext context, AppDbContext db, AppSettings settings, TimeProvider time)
    {
        var me = context.CurrentUser().Id;
        var now = time.GetUtcNow().UtcDateTime;
        var todayStart = SalonTime.StartOfDay(SalonTime.DayOf(now, settings.SalonTimeZone), settings.SalonTimeZone);

        var active = await db.Sessions.FirstOrDefaultAsync(s => s.EmployeeId == me && s.Status == SessionStatus.Active);
        var lastCompleted = await db.Sessions.WithItems()
            .Where(s => s.EmployeeId == me && s.Status == SessionStatus.Completed)
            .OrderByDescending(s => s.FinishedAt)
            .FirstOrDefaultAsync();
        // newest first: the day is read backwards from the customer just done
        var today = await db.Sessions.WithItems()
            .Where(s => s.EmployeeId == me && s.Status == SessionStatus.Completed && s.StartedAt >= todayStart)
            .OrderByDescending(s => s.FinishedAt)
            .ToListAsync();

        return new
        {
            active = active is null ? null : SessionDto.From(active, withItems: false),
            lastCompleted = lastCompleted is null ? null : SessionDto.From(lastCompleted),
            lastCompletedEditable = lastCompleted is not null && WithinEditWindow(lastCompleted.FinishedAt, now),
            today = new
            {
                count = today.Count,
                revenueCents = today.Sum(s => s.Items.Sum(i => i.LineTotalCents)),
                sessions = today.Select(s => SessionDto.From(s)),
            },
            // the screen counts elapsed time against startedAt, which is the server's clock;
            // tablets drift by seconds, so they correct against this
            now,
        };
    }

    private static async Task<IResult> Start(HttpContext context, AppDbContext db, TimeProvider time)
    {
        var me = context.CurrentUser().Id;
        if (await db.Sessions.AnyAsync(s => s.EmployeeId == me && s.Status == SessionStatus.Active))
        {
            throw new ApiException(StatusCodes.Status409Conflict, "active_session_exists");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var session = new Session { EmployeeId = me, StartedAt = now };
        db.Sessions.Add(session);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (AppDbContext.IsUniqueViolation(e, AppDbContext.OneActiveSessionIndex))
        {
            // a second tap that got past the check above
            throw new ApiException(StatusCodes.Status409Conflict, "active_session_exists");
        }

        return Results.Created($"/api/sessions/{session.Id}", new { session = SessionDto.From(session, withItems: false), now });
    }

    private static async Task<object> Finish(int id, FinishRequest body, HttpContext context, AppDbContext db, TimeProvider time)
    {
        var session = await db.Sessions.FindAsync(id);
        if (session is null || session.EmployeeId != context.CurrentUser().Id) throw ApiException.NotFound();
        if (session.Status != SessionStatus.Active) throw new ApiException(StatusCodes.Status409Conflict, "not_active");

        var ids = body.Items.Select(i => i.ServiceId).ToList();
        var services = await db.Services.Where(s => ids.Contains(s.Id) && s.Active).ToDictionaryAsync(s => s.Id);
        if (ids.Any(serviceId => !services.ContainsKey(serviceId)))
        {
            throw new ApiException(StatusCodes.Status400BadRequest, "unknown_service");
        }

        // the price is snapshotted here: later price-list changes never touch this record
        foreach (var picked in body.Items)
        {
            var service = services[picked.ServiceId];
            session.Items.Add(PricedLine(picked, service, service.PriceCents));
        }
        session.Status = SessionStatus.Completed;
        session.FinishedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();

        return new { session = SessionDto.From(session) };
    }

    private static async Task<object> Cancel(int id, HttpContext context, AppDbContext db, TimeProvider time)
    {
        var user = context.CurrentUser();
        var session = await db.Sessions.WithItems().SingleOrDefaultAsync(s => s.Id == id);
        if (session is null || (!user.IsAdmin() && session.EmployeeId != user.Id)) throw ApiException.NotFound();
        if (session.Status == SessionStatus.Cancelled) throw new ApiException(StatusCodes.Status409Conflict, "already_cancelled");

        var now = time.GetUtcNow().UtcDateTime;
        if (!user.IsAdmin() && session.Status == SessionStatus.Completed && !WithinEditWindow(session.FinishedAt, now))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "edit_window_expired");
        }

        if (session.Status == SessionStatus.Completed) session.EditedAt = now;
        session.Status = SessionStatus.Cancelled;
        await db.SaveChangesAsync();
        return new { session = SessionDto.From(session) };
    }

    /// <summary>
    /// Replace the items of a finished session. Services already on the record keep their
    /// original price snapshot; newly added ones are priced from today's list. A custom
    /// item is read from the request again, so a mistyped amount can be corrected.
    /// </summary>
    private static async Task<object> CorrectItems(
        int id, FinishRequest body, HttpContext context, AppDbContext db, TimeProvider time)
    {
        var user = context.CurrentUser();
        var session = await db.Sessions.WithItems().SingleOrDefaultAsync(s => s.Id == id);
        if (session is null || (!user.IsAdmin() && session.EmployeeId != user.Id)) throw ApiException.NotFound();
        if (session.Status != SessionStatus.Completed) throw new ApiException(StatusCodes.Status409Conflict, "not_completed");

        var now = time.GetUtcNow().UtcDateTime;
        if (!user.IsAdmin() && !WithinEditWindow(session.FinishedAt, now))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, "edit_window_expired");
        }

        var snapshots = new Dictionary<int, int>();
        foreach (var item in session.Items) snapshots[item.ServiceId] = item.PriceCentsSnapshot;

        var ids = body.Items.Select(i => i.ServiceId).ToList();
        var services = await db.Services.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);
        foreach (var picked in body.Items)
        {
            // one already on the record may have been deactivated since it was saved;
            // anything newly added has to be something the picker offers today
            var allowed = services.TryGetValue(picked.ServiceId, out var service)
                && (snapshots.ContainsKey(picked.ServiceId) || user.IsAdmin() || service.Active);
            if (!allowed) throw new ApiException(StatusCodes.Status400BadRequest, "unknown_service");
        }

        db.SessionItems.RemoveRange(session.Items);
        session.Items.Clear();
        foreach (var picked in body.Items)
        {
            var service = services[picked.ServiceId];
            var price = snapshots.TryGetValue(service.Id, out var kept) ? kept : service.PriceCents;
            session.Items.Add(PricedLine(picked, service, price));
        }
        session.EditedAt = now;
        await db.SaveChangesAsync();

        return new { session = SessionDto.From(session) };
    }

    /// <summary>Owner: browse and filter every record.</summary>
    private static async Task<object> List(
        string? from, string? to, int? employeeId, string? status, int? page, int? pageSize,
        AppDbContext db, AppSettings settings)
    {
        var pageNumber = page ?? 1;
        var size = pageSize ?? 20;
        if (pageNumber < 1 || size is < 1 or > 100) throw ApiException.Validation();

        var zone = settings.SalonTimeZone;
        var query = db.Sessions.AsQueryable();
        if (from is not null)
        {
            var start = SalonTime.StartOfDay(SalonTime.ParseDay(from), zone);
            query = query.Where(s => s.StartedAt >= start);
        }
        if (to is not null)
        {
            var end = SalonTime.StartOfDay(SalonTime.ParseDay(to).AddDays(1), zone);
            query = query.Where(s => s.StartedAt < end);
        }
        if (employeeId is { } employee) query = query.Where(s => s.EmployeeId == employee);
        if (status is not null)
        {
            var wanted = Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var parsed)
                ? parsed
                : throw ApiException.Validation();
            query = query.Where(s => s.Status == wanted);
        }

        var total = await query.CountAsync();
        var rows = await query.WithItems()
            .Include(s => s.Employee)
            .OrderByDescending(s => s.StartedAt)
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .AsSplitQuery()
            .ToListAsync();

        return new
        {
            total,
            page = pageNumber,
            pageSize = size,
            sessions = rows.Select(s => SessionDto.From(s, withEmployee: true)),
        };
    }

    /// <summary>
    /// Owner: enter a customer that never made it onto a tablet (device down, someone
    /// forgot). Prices come from today's list - there is no price history to look up.
    /// </summary>
    private static async Task<IResult> Create(CreateSessionRequest body, AppDbContext db, TimeProvider time)
    {
        if (body.FinishedAt <= body.StartedAt) throw new ApiException(StatusCodes.Status400BadRequest, "end_before_start");
        var employee = await LiveEmployee(db, body.EmployeeId);

        var ids = body.Items.Select(i => i.ServiceId).ToList();
        var services = await db.Services.Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id);

        var session = new Session
        {
            Employee = employee,
            StartedAt = body.StartedAt.UtcDateTime,
            FinishedAt = body.FinishedAt.UtcDateTime,
            Status = SessionStatus.Completed,
            EditedAt = time.GetUtcNow().UtcDateTime, // entered by hand, not recorded live
        };
        foreach (var picked in body.Items)
        {
            if (!services.TryGetValue(picked.ServiceId, out var service))
            {
                throw new ApiException(StatusCodes.Status400BadRequest, "unknown_service");
            }
            session.Items.Add(PricedLine(picked, service, service.PriceCents));
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return Results.Created($"/api/sessions/{session.Id}", new { session = SessionDto.From(session, withEmployee: true) });
    }

    /// <summary>
    /// Owner: fix who a record belongs to, or when it happened. The body is read as JSON
    /// because <c>"finishedAt": null</c> (reopen) and a missing field mean different things.
    /// </summary>
    private static async Task<object> Edit(int id, JsonObject body, AppDbContext db, TimeProvider time)
    {
        var session = await db.Sessions.WithItems().Include(s => s.Employee).SingleOrDefaultAsync(s => s.Id == id)
            ?? throw ApiException.NotFound();

        var employeeId = ReadInt(body, "employeeId");
        var startedAt = ReadInstant(body, "startedAt") ?? session.StartedAt;
        var finishedAt = body.ContainsKey("finishedAt") ? ReadInstant(body, "finishedAt") : session.FinishedAt;
        if (finishedAt is { } end && end <= startedAt) throw new ApiException(StatusCodes.Status400BadRequest, "end_before_start");

        if (employeeId is { } newEmployee) session.Employee = await LiveEmployee(db, newEmployee);
        session.StartedAt = startedAt;
        session.FinishedAt = finishedAt;
        session.EditedAt = time.GetUtcNow().UtcDateTime;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException e) when (AppDbContext.IsUniqueViolation(e, AppDbContext.OneActiveSessionIndex))
        {
            // moving an open session onto someone who already has one
            throw new ApiException(StatusCodes.Status409Conflict, "active_session_exists");
        }
        return new { session = SessionDto.From(session, withEmployee: true) };
    }

    /// <summary>Owner: destroy a record for good (its items go with it). Cancelling is the softer option.</summary>
    private static async Task<object> Delete(int id, AppDbContext db)
    {
        var deleted = await db.Sessions.Where(s => s.Id == id).ExecuteDeleteAsync();
        if (deleted == 0) throw ApiException.NotFound();
        return new { ok = true };
    }

    private static async Task<User> LiveEmployee(AppDbContext db, int id)
    {
        var user = await db.Users.FindAsync(id);
        if (user is null || user.DeletedAt is not null) throw new ApiException(StatusCodes.Status400BadRequest, "unknown_employee");
        return user;
    }

    private static int? ReadInt(JsonObject body, string name)
    {
        if (body[name] is not JsonValue value) return null;
        return value.TryGetValue<int>(out var number) && number > 0 ? number : throw ApiException.Validation();
    }

    private static DateTime? ReadInstant(JsonObject body, string name)
    {
        if (body[name] is not JsonValue value) return null;
        return value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var instant)
            ? instant.UtcDateTime
            : throw ApiException.Validation();
    }
}
