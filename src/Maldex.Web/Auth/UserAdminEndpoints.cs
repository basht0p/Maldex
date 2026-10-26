using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Web.Auth;

public sealed record ResetPasswordRequest(string? NewPassword);
public sealed record AuthSettingsDto(bool RequireTotpForLocal);

/// <summary>User and sign-in settings administration under <c>/api/admin</c>.</summary>
public static class UserAdminEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/admin/users").RequireAuthorization(AuthSetup.AdminPolicy);

        users.MapGet("", async (UserAdminService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct)));

        users.MapPost("", async (CreateUserRequest req, UserAdminService svc, CurrentUser current, CancellationToken ct) =>
        {
            var result = await svc.CreateAsync(req, current.UserId, ct);
            return result.Ok ? Results.Created($"/api/admin/users/{result.User!.Id}", result.User) : Failure(result);
        });

        users.MapPatch("{id:guid}", async (Guid id, UpdateUserRequest req, UserAdminService svc, CurrentUser current, CancellationToken ct) =>
        {
            var result = await svc.UpdateAsync(id, req, current.UserId, ct);
            return result.Ok ? Results.Ok(result.User) : Failure(result);
        });

        users.MapPost("{id:guid}/reset-password", async (Guid id, ResetPasswordRequest req, UserAdminService svc, CurrentUser current, CancellationToken ct) =>
        {
            var result = await svc.ResetPasswordAsync(id, req.NewPassword ?? "", resetTotp: false, mustChange: true, current.UserId, ct);
            return result.Ok ? Results.NoContent() : Failure(result);
        });

        users.MapPost("{id:guid}/clear-totp", async (Guid id, UserAdminService svc, CurrentUser current, CancellationToken ct) =>
        {
            var result = await svc.ClearTotpAsync(id, current.UserId, ct);
            return result.Ok ? Results.NoContent() : Failure(result);
        });

        var settings = app.MapGroup("/api/admin/settings").RequireAuthorization(AuthSetup.AdminPolicy);

        settings.MapGet("", async (MaldexDbContext db, CancellationToken ct) =>
            Results.Ok(new AuthSettingsDto((await AuthEndpoints.SettingsAsync(db, ct)).RequireTotpForLocal)));

        settings.MapPut("", async (AuthSettingsDto req, MaldexDbContext db, AuditWriter audit, CurrentUser current, CancellationToken ct) =>
        {
            var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Id == 1, ct);
            if (row is null) db.SystemSettings.Add(row = new SystemSettings { Id = 1 });
            row.RequireTotpForLocal = req.RequireTotpForLocal;
            await audit.WriteAsync("settings.update", "system", $"requireTotpForLocal={req.RequireTotpForLocal}", current.UserId, ct);
            return Results.Ok(new AuthSettingsDto(row.RequireTotpForLocal));
        });
    }

    private static IResult Failure(UserAdminResult r) => AuthEndpoints.Error(r.Error switch
    {
        "not_found" => StatusCodes.Status404NotFound,
        "duplicate_user_name" or "concurrent_update" or "last_admin" or "role_managed_by_idp" or "break_glass_requires_local_admin" => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    }, r.Error ?? "invalid");
}
