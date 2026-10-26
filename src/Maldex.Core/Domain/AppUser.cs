using Microsoft.AspNetCore.Identity;

namespace Maldex.Core.Domain;

/// <summary>A Maldex account. Rows live in the <c>Users</c> table; ASP.NET Core Identity supplies the credential columns.</summary>
public class AppUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";

    /// <summary>Null means the account may sign in but holds no Maldex role (for example a fresh SSO account).</summary>
    public UserRole? Role { get; set; }

    public UserKind Kind { get; set; }
    public bool IsDisabled { get; set; }
    public bool IsBreakGlass { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
}
