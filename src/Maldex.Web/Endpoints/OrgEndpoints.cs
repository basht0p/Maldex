using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Maldex.Web.Auth;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Endpoints;

public sealed record OrgDto(Guid Id, string Name, int RetentionDays);
public sealed record OrgRequest(string? Name, int? RetentionDays);

public static class OrgEndpoints
{
    public static IEndpointRouteBuilder MapOrgEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs");

        g.MapGet("", async (MaldexDbContext db, CancellationToken ct) =>
            await db.Orgs.AsNoTracking().OrderBy(o => o.Name)
                .Select(o => new OrgDto(o.Id, o.Name, o.RetentionDays)).ToListAsync(ct))
            .RequireAuthorization(AuthSetup.AnalystPolicy);

        g.MapPost("", async (OrgRequest req, MaldexDbContext db, AuditWriter audit, CancellationToken ct) =>
        {
            if (Validate(req) is { } errors) return Results.ValidationProblem(errors);
            var name = req.Name!.Trim();
            if (await db.Orgs.AnyAsync(o => o.Name == name, ct)) return Duplicate();

            var org = new Org { Id = Guid.NewGuid(), Name = name, RetentionDays = req.RetentionDays!.Value };
            db.Orgs.Add(org);
            try
            {
                await audit.WriteAsync("org.create", org.Id.ToString(), $"name={org.Name}; retentionDays={org.RetentionDays}", ct);
            }
            catch (DbUpdateException ex) when (CurrentUser.IsUniqueViolation(ex))
            {
                return Duplicate();
            }
            return Results.Created($"/api/orgs/{org.Id}", new OrgDto(org.Id, org.Name, org.RetentionDays));
        }).RequireAuthorization(AuthSetup.AdminPolicy);

        g.MapPut("{id:guid}", async (Guid id, OrgRequest req, MaldexDbContext db, AuditWriter audit, CancellationToken ct) =>
        {
            if (Validate(req) is { } errors) return Results.ValidationProblem(errors);
            var org = await db.Orgs.FirstOrDefaultAsync(o => o.Id == id, ct);
            if (org is null) return Results.NotFound();
            var name = req.Name!.Trim();
            if (await db.Orgs.AnyAsync(o => o.Name == name && o.Id != id, ct)) return Duplicate();

            org.Name = name;
            org.RetentionDays = req.RetentionDays!.Value;
            try
            {
                await audit.WriteAsync("org.update", org.Id.ToString(), $"name={org.Name}; retentionDays={org.RetentionDays}", ct);
            }
            catch (DbUpdateException ex) when (CurrentUser.IsUniqueViolation(ex))
            {
                return Duplicate();
            }
            return Results.Ok(new OrgDto(org.Id, org.Name, org.RetentionDays));
        }).RequireAuthorization(AuthSetup.AdminPolicy);

        return app;
    }

    private static IResult Duplicate() =>
        Results.Problem(title: "An organization with this name already exists.", statusCode: StatusCodes.Status409Conflict);

    private static Dictionary<string, string[]>? Validate(OrgRequest req)
    {
        var errors = new Dictionary<string, string[]>();
        var name = req.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            errors["name"] = ["Name is required and must be 1-200 characters."];
        if (req.RetentionDays is not (>= 1 and <= 3650))
            errors["retentionDays"] = ["Retention must be between 1 and 3650 days."];
        return errors.Count == 0 ? null : errors;
    }
}
