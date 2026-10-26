using Maldex.Core.Domain;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>Builds unsaved <see cref="AppUser"/> rows for tests; every call gets a unique user name and security stamp.</summary>
public static class TestUsers
{
    public static AppUser Create(UserRole? role, string displayName = "Test user", UserKind kind = UserKind.Local)
    {
        var id = Guid.NewGuid();
        var name = "test-" + id.ToString("N");
        return new AppUser
        {
            Id = id,
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            Role = role,
            Kind = kind,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
