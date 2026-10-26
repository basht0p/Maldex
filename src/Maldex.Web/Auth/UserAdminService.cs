using System.Text.RegularExpressions;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Data;

namespace Maldex.Web.Auth;

public sealed record UserDto(Guid Id, string UserName, string DisplayName, string Kind, string? Role, bool IsDisabled, bool TotpEnabled,
    bool IsBreakGlass, bool RoleManagedByIdp, DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt);

public sealed record CreateUserRequest(string UserName, string DisplayName, string Role, string Password, bool MustChangePassword = true, bool IsBreakGlass = false);

/// <summary><c>Role</c> is <c>Admin</c>, <c>Analyst</c> or <c>none</c>; a null member is left unchanged.</summary>
public sealed record UpdateUserRequest(string? DisplayName, string? Role, bool? IsDisabled, bool? IsBreakGlass);

/// <summary>The outcome of a user administration call; <see cref="Error"/> is the API error code when not <see cref="Ok"/>.</summary>
public sealed record UserAdminResult(bool Ok, string? Error, UserDto? User)
{
    public static UserAdminResult Success(UserDto? user = null) => new(true, null, user);
    public static UserAdminResult Fail(string error) => new(false, error, null);
}

/// <summary>
/// Every user administration rule, shared by the API and the admin CLI: user name and password validation, the last-admin guard,
/// the break-glass and IdP-managed-role rules, security-stamp refresh and the audit rows. A null actor means the CLI.
/// </summary>
public sealed partial class UserAdminService(UserManager<AppUser> users, MaldexDbContext db, AuditWriter audit, IOptions<MaldexAuthOptions> options, TimeProvider time)
{
    [GeneratedRegex("^[A-Za-z0-9._@-]{1,64}$")]
    private static partial Regex UserNamePattern();

    private MaldexAuthOptions Auth => options.Value;

    public UserDto ToDto(AppUser u) => new(u.Id, u.UserName ?? "", u.DisplayName, u.Kind.ToString(), u.Role?.ToString(), u.IsDisabled, u.TwoFactorEnabled,
        u.IsBreakGlass, u.Kind == UserKind.Sso && !string.IsNullOrEmpty(Auth.Oidc.RoleClaim), u.CreatedAt, u.LastSignInAt);

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct)
    {
        var all = await db.Users.AsNoTracking().OrderBy(u => u.NormalizedUserName).ToListAsync(ct);
        return all.Select(ToDto).ToList();
    }

    /// <summary>The user with this name (trimmed, case-insensitive), tracked; null if there is none.</summary>
    public async Task<AppUser?> FindByNameAsync(string userName) => await users.FindByNameAsync((userName ?? "").Trim());

    public async Task<UserAdminResult> CreateAsync(CreateUserRequest r, Guid? actorId, CancellationToken ct = default)
    {
        var userName = (r.UserName ?? "").Trim();
        if (!UserNamePattern().IsMatch(userName)) return UserAdminResult.Fail("invalid_user_name");
        if (!TryParseRole(r.Role, out var role) || role is null) return UserAdminResult.Fail("invalid_role");
        var displayName = string.IsNullOrWhiteSpace(r.DisplayName) ? userName : r.DisplayName.Trim();
        if (displayName.Length > 200) return UserAdminResult.Fail("invalid_display_name");
        if (r.IsBreakGlass && role != UserRole.Admin) return UserAdminResult.Fail("break_glass_requires_local_admin");
        if (await users.FindByNameAsync(userName) is not null) return UserAdminResult.Fail("duplicate_user_name");

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = displayName,
            Role = role,
            Kind = UserKind.Local,
            IsBreakGlass = r.IsBreakGlass,
            MustChangePassword = r.MustChangePassword,
            CreatedAt = time.GetUtcNow(),
        };
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, r.Password ?? "");
        }
        catch (DbUpdateException ex) when (CurrentUser.IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            return UserAdminResult.Fail("duplicate_user_name");
        }
        if (!created.Succeeded) return UserAdminResult.Fail(CreateErrorCode(created));

        await audit.WriteAsync("user.create", userName, $"role={role}; breakGlass={r.IsBreakGlass}; mustChangePassword={r.MustChangePassword}", actorId, ct);
        return UserAdminResult.Success(ToDto(user));
    }

    public async Task<UserAdminResult> UpdateAsync(Guid id, UpdateUserRequest r, Guid? actorId, CancellationToken ct = default)
    {
        // The last-admin check and the save run in one SERIALIZABLE transaction, so two concurrent demotions cannot both pass.
        // A deadlock victim (1205) is retried once, against whatever the winner committed. The audit row is written after the
        // loop: a failure there must surface, not be retried into a "nothing to change" success with the row lost.
        UserAdminResult result;
        string? details;
        for (var attempt = 0; ; attempt++)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try
            {
                (result, details) = await UpdateCoreAsync(id, r, ct);
                if (details is null) return result; // refused, or nothing to change: nothing written
                await tx.CommitAsync(ct);
                break;
            }
            catch (Exception ex) when (IsDeadlock(ex))
            {
                db.ChangeTracker.Clear();
                if (attempt >= 1) return UserAdminResult.Fail("concurrent_update");
            }
        }
        await audit.WriteAsync("user.update", result.User!.UserName, details, actorId, ct);
        return result;
    }

    private static bool IsDeadlock(Exception ex) => ex.GetBaseException() is SqlException { Number: 1205 };

    private async Task<(UserAdminResult result, string? details)> UpdateCoreAsync(Guid id, UpdateUserRequest r, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return (UserAdminResult.Fail("not_found"), null);

        var role = user.Role;
        if (r.Role is not null)
        {
            if (!TryParseRole(r.Role, out var parsed)) return (UserAdminResult.Fail("invalid_role"), null);
            if (parsed != user.Role && user.Kind == UserKind.Sso && !string.IsNullOrEmpty(Auth.Oidc.RoleClaim))
                return (UserAdminResult.Fail("role_managed_by_idp"), null);
            role = parsed;
        }
        var displayName = user.DisplayName;
        if (r.DisplayName is not null)
        {
            displayName = r.DisplayName.Trim();
            if (displayName.Length is 0 or > 200) return (UserAdminResult.Fail("invalid_display_name"), null);
        }

        var after = new AppUser
        {
            Id = user.Id, Kind = user.Kind, Role = role,
            IsDisabled = r.IsDisabled ?? user.IsDisabled,
            // Break-glass only means something on a local Admin: leaving that role (or being disabled elsewhere) drops the flag unless the
            // request asks for it explicitly, which is then refused below.
            IsBreakGlass = r.IsBreakGlass ?? (user.IsBreakGlass && role == UserRole.Admin),
        };
        if (after.IsBreakGlass && (after.Kind != UserKind.Local || after.Role != UserRole.Admin))
            return (UserAdminResult.Fail("break_glass_requires_local_admin"), null);

        // Only a change that takes away an Admin who could sign in is guarded; an already-locked-out installation can still be edited.
        var wasUsableAdmin = user.Role == UserRole.Admin && AdminGuard.CanSignIn(user, Auth);
        if (wasUsableAdmin && !(after.Role == UserRole.Admin && AdminGuard.CanSignIn(after, Auth))
            && !await AdminGuard.LeavesAnAdminAsync(db, Auth, user.Id, after, ct))
            return (UserAdminResult.Fail("last_admin"), null);

        var changes = new List<string>();
        if (displayName != user.DisplayName) changes.Add("displayName");
        if (role != user.Role) changes.Add($"role {user.Role?.ToString() ?? "none"} -> {role?.ToString() ?? "none"}");
        if (after.IsDisabled != user.IsDisabled) changes.Add($"isDisabled -> {after.IsDisabled}");
        if (after.IsBreakGlass != user.IsBreakGlass)
            changes.Add($"isBreakGlass -> {after.IsBreakGlass}" + (r.IsBreakGlass is null ? " (cleared: role no longer Admin)" : ""));
        if (changes.Count == 0) return (UserAdminResult.Success(ToDto(user)), null);

        user.DisplayName = displayName;
        user.Role = role;
        user.IsDisabled = after.IsDisabled;
        user.IsBreakGlass = after.IsBreakGlass;
        await SaveWithNewStampAsync(user, ct);
        return (UserAdminResult.Success(ToDto(user)), string.Join("; ", changes));
    }

    /// <summary>
    /// Sets a new password on a local user, clears any lockout, sets "must change password" as <paramref name="mustChange"/>, and
    /// with <paramref name="resetTotp"/> also clears TOTP. Ends the user's sessions at their next security-stamp check.
    /// </summary>
    public async Task<UserAdminResult> ResetPasswordAsync(Guid id, string newPassword, bool resetTotp, bool mustChange, Guid? actorId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return UserAdminResult.Fail("not_found");
        if (user.Kind != UserKind.Local) return UserAdminResult.Fail("not_local_user");

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var reset = await users.ResetPasswordAsync(user, token, newPassword ?? "");
        if (!reset.Succeeded) return UserAdminResult.Fail(AuthEndpoints.PasswordErrorCode(reset));

        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        if (resetTotp) await DisableTotpAsync(user);
        user.MustChangePassword = mustChange;
        await SaveWithNewStampAsync(user, ct);
        await audit.WriteAsync("user.reset_password", user.UserName ?? "", $"mustChange={mustChange}; resetTotp={resetTotp}", actorId, ct);
        return UserAdminResult.Success(ToDto(user));
    }

    /// <summary>Disables TOTP, replaces the authenticator key and deletes the recovery codes and the last accepted TOTP step.</summary>
    public async Task<UserAdminResult> ClearTotpAsync(Guid id, Guid? actorId, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null) return UserAdminResult.Fail("not_found");

        await DisableTotpAsync(user);
        await SaveWithNewStampAsync(user, ct);
        await audit.WriteAsync("user.clear_totp", user.UserName ?? "", null, actorId, ct);
        return UserAdminResult.Success(ToDto(user));
    }

    private async Task DisableTotpAsync(AppUser user)
    {
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await users.RemoveAuthenticationTokenAsync(user, MaldexUserStore.InternalLoginProvider, MaldexUserStore.RecoveryCodesTokenName);
        await users.RemoveAuthenticationTokenAsync(user, TotpEndpoints.StepProvider, TotpEndpoints.StepToken);
    }

    /// <summary>
    /// Saves the tracked <paramref name="user"/> with a fresh security stamp. It goes through the context rather than
    /// <see cref="UserManager{TUser}"/> so the user validators cannot refuse a change to an existing (legacy or SSO) row.
    /// </summary>
    private async Task SaveWithNewStampAsync(AppUser user, CancellationToken ct)
    {
        user.SecurityStamp = Guid.NewGuid().ToString();
        await db.SaveChangesAsync(ct);
    }

    private static bool TryParseRole(string? value, out UserRole? role)
    {
        role = null;
        if (string.Equals(value, "none", StringComparison.OrdinalIgnoreCase)) return true;
        if (Enum.TryParse<UserRole>(value, true, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(value, out _))
        {
            role = parsed;
            return true;
        }
        return false;
    }

    private static string CreateErrorCode(IdentityResult result)
    {
        var codes = result.Errors.Select(e => e.Code).ToList();
        if (codes.Contains(nameof(IdentityErrorDescriber.DuplicateUserName))) return "duplicate_user_name";
        if (codes.Any(c => c is nameof(IdentityErrorDescriber.InvalidUserName) or "InvalidUserNameLength")) return "invalid_user_name";
        return AuthEndpoints.PasswordErrorCode(result);
    }
}
