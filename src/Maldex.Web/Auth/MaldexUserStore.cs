using System.Security.Cryptography;
using System.Text;
using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>
/// The Identity user store over <see cref="MaldexDbContext"/> (users, claims, logins and tokens; no role tables).
/// Recovery codes are kept as lowercase hex SHA-256 hashes, joined with <c>;</c>, in the token Identity uses for them.
/// </summary>
public class MaldexUserStore(MaldexDbContext context, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<AppUser, MaldexDbContext, Guid>(context, describer)
{
    /// <summary>The login-provider name Identity's user store gives its internal tokens (authenticator key, recovery codes).</summary>
    public const string InternalLoginProvider = "[AspNetUserStore]";
    public const string AuthenticatorKeyTokenName = "AuthenticatorKey";
    public const string RecoveryCodesTokenName = "RecoveryCodes";

    private const string CodeAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int CodeLength = 10;

    /// <summary>New recovery codes, shown as <c>xxxxx-xxxxx</c>: ten characters from <c>[a-z0-9]</c>, from a cryptographic RNG.</summary>
    public static string[] NewRecoveryCodes(int count = 10) =>
        [.. Enumerable.Range(0, count).Select(_ =>
        {
            var chars = RandomNumberGenerator.GetItems<char>(CodeAlphabet, CodeLength);
            return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
        })];

    /// <summary>Lowercase, without dashes or spaces: how a code is compared however the user typed it.</summary>
    public static string NormalizeCode(string? code) =>
        new((code ?? "").Where(c => c is not ('-' or ' ')).Select(char.ToLowerInvariant).ToArray());

    private static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeCode(code))));

    public override async Task ReplaceCodesAsync(AppUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        var hashes = string.Join(';', recoveryCodes.Select(Hash));
        await SetTokenAsync(user, InternalLoginProvider, RecoveryCodesTokenName, hashes, cancellationToken);
    }

    public override async Task<bool> RedeemCodeAsync(AppUser user, string code, CancellationToken cancellationToken)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length != CodeLength) return false;
        var stored = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodesTokenName, cancellationToken) ?? "";
        var hashes = stored.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var candidate = Encoding.ASCII.GetBytes(Hash(normalized));
        var index = -1;
        // Compare against every stored hash, so the time taken does not depend on which one (if any) matches.
        for (var i = 0; i < hashes.Count; i++)
            if (CryptographicOperations.FixedTimeEquals(candidate, Encoding.ASCII.GetBytes(hashes[i])) && index < 0) index = i;
        if (index < 0) return false;
        hashes.RemoveAt(index);
        await SetTokenAsync(user, InternalLoginProvider, RecoveryCodesTokenName, string.Join(';', hashes), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(AppUser user, CancellationToken cancellationToken)
    {
        var stored = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodesTokenName, cancellationToken) ?? "";
        return stored.Length == 0 ? 0 : stored.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
