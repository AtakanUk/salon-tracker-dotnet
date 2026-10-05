using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SalonTracker.Api.Auth;
using SalonTracker.Api.Contracts;
using SalonTracker.Api.Data;
using SalonTracker.Api.Infrastructure;

namespace SalonTracker.Api.Features;

public sealed record CreateServiceRequest(string NameTr, string NameDe, int PriceCents, int? SortOrder);

public sealed record UpdateServiceRequest(string? NameTr, string? NameDe, int? PriceCents, int? SortOrder, bool? Active);

public sealed class CreateServiceValidator : AbstractValidator<CreateServiceRequest>
{
    public CreateServiceValidator()
    {
        RuleFor(x => x.NameTr).NotEmpty().MaximumLength(80);
        RuleFor(x => x.NameDe).NotEmpty().MaximumLength(80);
        RuleFor(x => x.PriceCents).InclusiveBetween(0, 1_000_000);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000);
    }
}

public sealed class UpdateServiceValidator : AbstractValidator<UpdateServiceRequest>
{
    public UpdateServiceValidator()
    {
        RuleFor(x => x.NameTr).NotEmpty().MaximumLength(80).When(x => x.NameTr is not null);
        RuleFor(x => x.NameDe).NotEmpty().MaximumLength(80).When(x => x.NameDe is not null);
        RuleFor(x => x.PriceCents).InclusiveBetween(0, 1_000_000);
        RuleFor(x => x.SortOrder).InclusiveBetween(0, 10_000);
    }
}

/// <summary>The price list. Services are deactivated, never deleted.</summary>
public static class ServiceEndpoints
{
    public static void MapServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var services = app.MapGroup("/api/services").WithTags("Price list").RequireAuthorization();

        services.MapGet("", List);
        services.MapPost("", Create).RequireAuthorization(AuthSetup.AdminPolicy).Validate<CreateServiceRequest>();
        services.MapPatch("/{id:int}", Update).RequireAuthorization(AuthSetup.AdminPolicy).Validate<UpdateServiceRequest>();
    }

    /// <summary>Employees get the active price list; the owner can ask for all with <c>?all=1</c>.</summary>
    private static async Task<object> List(string? all, HttpContext context, AppDbContext db)
    {
        var includeInactive = all == "1" && context.CurrentUser().IsAdmin();
        var list = await db.Services
            .Where(s => includeInactive || s.Active)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Id)
            .ToListAsync();
        return new { services = list.Select(ServiceDto.From) };
    }

    private static async Task<IResult> Create(CreateServiceRequest body, AppDbContext db, TimeProvider time)
    {
        var service = new Service
        {
            NameTr = body.NameTr,
            NameDe = body.NameDe,
            PriceCents = body.PriceCents,
            SortOrder = body.SortOrder ?? 0,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Services.Add(service);
        await db.SaveChangesAsync();
        return Results.Created($"/api/services/{service.Id}", new { service = ServiceDto.From(service) });
    }

    private static async Task<object> Update(int id, UpdateServiceRequest body, AppDbContext db)
    {
        var service = await db.Services.FindAsync(id) ?? throw ApiException.NotFound();
        if (body.NameTr is not null) service.NameTr = body.NameTr;
        if (body.NameDe is not null) service.NameDe = body.NameDe;
        if (body.PriceCents is { } price) service.PriceCents = price;
        if (body.SortOrder is { } order) service.SortOrder = order;
        if (body.Active is { } active) service.Active = active;
        await db.SaveChangesAsync();
        return new { service = ServiceDto.From(service) };
    }
}
