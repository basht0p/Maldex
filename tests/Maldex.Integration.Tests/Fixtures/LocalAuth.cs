using System.Net.Http.Json;
using Maldex.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Maldex.Integration.Tests.Fixtures;

/// <summary>Helpers for factories in <c>authMode: "Local"</c>: seed accounts through <see cref="UserManager{TUser}"/> and sign in with cookies.</summary>
public static class LocalAuth
{
    /// <summary>Creates a local account with <paramref name="password"/>; <paramref name="tweak"/> runs before the row is created.</summary>
    public static async Task<AppUser> SeedUserAsync(MaldexAppFactory f, string userName, string password, UserRole? role, Action<AppUser>? tweak = null)
    {
        await using var scope = f.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = userName,
            Role = role,
            Kind = UserKind.Local,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        tweak?.Invoke(user);
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Code + ": " + e.Description)));
        return user;
    }

    /// <summary>
    /// A client (with a cookie jar, and the CSRF header every factory client sends) after <c>POST /api/auth/login</c> succeeded.
    /// The session may be partial: the login answer's <c>next</c> is not checked.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(MaldexAppFactory f, string userName, string password)
    {
        var client = f.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/auth/login", new { userName, password }, TestContext.Current.CancellationToken);
        Assert.True(resp.IsSuccessStatusCode, $"login as {userName} failed: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        return client;
    }
}
