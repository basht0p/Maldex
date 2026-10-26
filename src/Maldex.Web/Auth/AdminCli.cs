using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Maldex.Core.Domain;
using Maldex.Data;
using Maldex.Web.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maldex.Web.Auth;

/// <summary>
/// <c>Maldex.Web admin &lt;command&gt;</c>: bootstrap and recovery of local accounts against the configured database, without
/// starting the web host. Exit codes: 0 done, 1 refused, 64 usage error, 78 configuration error. Every rule is
/// <see cref="UserAdminService"/>'s, the same code the admin API runs.
/// </summary>
public static class AdminCli
{
    public const int Ok = 0;
    public const int Refused = 1;
    public const int UsageError = 64;
    public const int ConfigError = StartupChecks.ConfigErrorExitCode;

    internal const string AuditPrefix = "via maldex-admin";

    public const string Usage = """
        usage: Maldex.Web admin <command>

          create-user <name> [--admin|--analyst] [--display-name <s>] [--break-glass] [--password-stdin]
          reset-password <name> [--reset-totp] [--password-stdin]
          set-break-glass <name> on|off
          disable <name>
          enable <name>
          set-role <name> admin|analyst|none
          list [--json]

        Passwords are prompted twice with no echo, or read from standard input with --password-stdin
        (one trailing newline is removed). They are never accepted as command-line arguments.
        """;

    private static readonly string[] Commands = ["create-user", "reset-password", "set-break-glass", "disable", "enable", "set-role", "list"];

    private static readonly Dictionary<string, string> Messages = new()
    {
        ["invalid_user_name"] = "user names are 1 to 64 characters from A-Z a-z 0-9 . _ @ -",
        ["duplicate_user_name"] = "a user with that name already exists (names are not case-sensitive)",
        ["password_too_short"] = $"passwords must be at least {LocalAuthSetup.MinPasswordLength} characters",
        ["password_too_long"] = $"passwords must be at most {LocalAuthSetup.MaxPasswordLength} characters",
        ["invalid_password"] = "the password was refused",
        ["invalid_role"] = "the role must be admin, analyst or none",
        ["invalid_display_name"] = "display names are 1 to 200 characters",
        ["break_glass_requires_local_admin"] = "break-glass applies only to local users with the Admin role",
        ["last_admin"] = "this would leave no enabled Admin who can sign in",
        ["role_managed_by_idp"] = "this user's role is managed by the identity provider",
        ["not_found"] = "no such user",
        ["not_local_user"] = "only local users have a password",
        ["concurrent_update"] = "the user was changed at the same time; try again",
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args, IConfiguration config, IHostEnvironment env, TextReader stdin, TextWriter stdout,
        TextWriter stderr, IPasswordPrompt prompt, CancellationToken ct)
    {
        if (args.Length == 0 || Array.IndexOf(Commands, args[0]) < 0)
            return Fail(stderr, args.Length == 0 ? "no command given" : $"unknown command '{args[0]}'");
        if (Parse(args, out var parseError) is not { } cmd) return Fail(stderr, parseError!);

        var connection = config.GetConnectionString("Maldex");
        if (string.IsNullOrWhiteSpace(connection))
        {
            await stderr.WriteLineAsync(StartupChecks.MissingConnectionMessage);
            return ConfigError;
        }

        var audit = new AuditPrefixInterceptor($"{AuditPrefix}: {string.Join(' ', args)}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<MaldexDbContext>(o => o.UseSqlServer(connection).AddInterceptors(audit));
        services.AddMaldexIdentityCore(config);
        // The CLI's tokens (password reset) live and die inside this process; nothing is written to a key ring.
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<IOptions<MaldexAuthOptions>>(Options.Create(MaldexAuthOptions.Read(config)));
        services.AddScoped<CurrentUser>(); // never resolved: audit rows have no user
        services.AddScoped<AuditWriter>();
        services.AddScoped<UserAdminService>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        try
        {
            try
            {
                await sp.GetRequiredService<MaldexDbContext>().Database.MigrateAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stderr.WriteLineAsync($"error: cannot open or migrate the database: {ex.GetBaseException().Message}");
                return ConfigError;
            }
            return await new Session(sp.GetRequiredService<UserAdminService>(), sp.GetRequiredService<MaldexDbContext>(),
                sp.GetRequiredService<IOptions<MaldexAuthOptions>>().Value, stdin, stdout, stderr, prompt, ct).RunAsync(cmd);
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync("cancelled");
            return Refused;
        }
        catch (NoTerminalException ex)
        {
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return UsageError;
        }
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"error: {message}");
        stderr.WriteLine(Usage);
        return UsageError;
    }

    private sealed record Command(string Name, string? UserName, string? Argument, HashSet<string> Flags, string? DisplayName);

    private static Command? Parse(string[] args, out string? parseError)
    {
        parseError = null;
        var name = args[0];
        var allowed = name switch
        {
            "create-user" => new[] { "--admin", "--analyst", "--break-glass", "--password-stdin", "--display-name" },
            "reset-password" => ["--reset-totp", "--password-stdin"],
            "list" => ["--json"],
            _ => Array.Empty<string>(),
        };
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var positional = new List<string>();
        string? displayName = null;
        for (var i = 1; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--password" || a.StartsWith("--password=", StringComparison.Ordinal))
            {
                parseError = "passwords are never accepted as arguments; use --password-stdin (or the prompt)";
                return null;
            }
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(a);
                continue;
            }
            if (Array.IndexOf(allowed, a) < 0)
            {
                parseError = $"unknown option '{a}' for {name}";
                return null;
            }
            if (a == "--display-name")
            {
                if (++i >= args.Length) { parseError = "--display-name needs a value"; return null; }
                displayName = args[i];
            }
            else if (!flags.Add(a))
            {
                parseError = $"{a} given twice";
                return null;
            }
        }

        if (flags.Contains("--admin") && flags.Contains("--analyst")) { parseError = "--admin and --analyst are exclusive"; return null; }
        var wanted = name switch
        {
            "list" => 0,
            "create-user" or "reset-password" or "disable" or "enable" => 1,
            _ => 2,
        };
        if (positional.Count != wanted)
        {
            parseError = wanted == 0 ? "list takes no arguments" : $"{name} takes {(wanted == 1 ? "a user name" : "a user name and one value")}";
            return null;
        }
        var value = wanted == 2 ? positional[1] : null;
        if (name == "set-break-glass" && value is not ("on" or "off")) { parseError = "set-break-glass takes on or off"; return null; }
        if (name == "set-role" && value is not ("admin" or "analyst" or "none")) { parseError = "set-role takes admin, analyst or none"; return null; }
        return new Command(name, wanted == 0 ? null : positional[0], value, flags, displayName);
    }

    private sealed class Session(UserAdminService users, MaldexDbContext db, MaldexAuthOptions auth, TextReader stdin, TextWriter stdout,
        TextWriter stderr, IPasswordPrompt prompt, CancellationToken ct)
    {
        public async Task<int> RunAsync(Command c) => c.Name switch
        {
            "create-user" => await CreateAsync(c),
            "reset-password" => await ResetPasswordAsync(c),
            "set-break-glass" => await UpdateAsync(c, new UpdateUserRequest(null, null, null, c.Argument == "on"), $"break-glass {c.Argument} for '{c.UserName}'"),
            "disable" => await UpdateAsync(c, new UpdateUserRequest(null, null, true, null), $"disabled '{c.UserName}'"),
            "enable" => await UpdateAsync(c, new UpdateUserRequest(null, null, false, null), $"enabled '{c.UserName}'"),
            "set-role" => await UpdateAsync(c, new UpdateUserRequest(null, c.Argument, null, null), $"role of '{c.UserName}' is now {c.Argument}"),
            _ => await ListAsync(c),
        };

        private async Task<int> CreateAsync(Command c)
        {
            var name = c.UserName!.Trim();
            // Refuse before asking for a password that cannot be used.
            if (!IsValidUserName(name)) return Refuse("invalid_user_name");
            if (await users.FindByNameAsync(name) is not null) return Refuse("duplicate_user_name");

            if (await ReadNewPasswordAsync(c) is not { } password) return Refused;
            var role = c.Flags.Contains("--admin") ? "Admin" : "Analyst";
            // The password is typed at the host, so the user need not change it at first sign-in.
            var result = await users.CreateAsync(new CreateUserRequest(name, c.DisplayName ?? "", role, password, MustChangePassword: false,
                IsBreakGlass: c.Flags.Contains("--break-glass")), null, ct);
            if (!result.Ok) return Refuse(result.Error!);
            await stdout.WriteLineAsync($"created {role} '{result.User!.UserName}'{(result.User.IsBreakGlass ? " (break-glass)" : "")}");
            return Ok;
        }

        private async Task<int> ResetPasswordAsync(Command c)
        {
            if (await users.FindByNameAsync(c.UserName!) is not { } user) return Refuse("not_found");
            if (await ReadNewPasswordAsync(c) is not { } password) return Refused;
            // Typed at the host, like create-user: no forced change at the next sign-in.
            var result = await users.ResetPasswordAsync(user.Id, password, c.Flags.Contains("--reset-totp"), mustChange: false, null, ct);
            if (!result.Ok) return Refuse(result.Error!);
            await stdout.WriteLineAsync($"password reset for '{result.User!.UserName}'; lockout cleared{(c.Flags.Contains("--reset-totp") ? "; TOTP cleared" : "")}");
            return Ok;
        }

        private async Task<int> UpdateAsync(Command c, UpdateUserRequest request, string done)
        {
            if (await users.FindByNameAsync(c.UserName!) is not { } user) return Refuse("not_found");
            var result = await users.UpdateAsync(user.Id, request, null, ct);
            if (!result.Ok) return Refuse(result.Error!);
            await stdout.WriteLineAsync(done);
            return Ok;
        }

        private async Task<int> ListAsync(Command c)
        {
            var all = await users.ListAsync(ct);
            if (c.Flags.Contains("--json"))
            {
                await stdout.WriteLineAsync(JsonSerializer.Serialize(all.Select(u => new
                {
                    u.UserName, u.DisplayName, u.Kind, u.Role, u.IsDisabled, u.TotpEnabled, u.IsBreakGlass, u.LastSignInAt,
                }), Json));
                await stdout.WriteLineAsync("#config " + JsonSerializer.Serialize(new { passwordLogin = auth.PasswordLogin, ssoEnabled = auth.Oidc.Enabled }, Json));
                return Ok;
            }

            var rows = all.Select(u => new[]
            {
                u.UserName, u.Kind, u.Role ?? "none", u.IsDisabled ? "disabled" : "enabled", u.TotpEnabled ? "totp" : "-",
                u.IsBreakGlass ? "break-glass" : "-", u.LastSignInAt?.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "never",
            }).Prepend(["NAME", "KIND", "ROLE", "STATE", "TOTP", "BREAK-GLASS", "LAST SIGN-IN"]).ToList();
            var widths = Enumerable.Range(0, 7).Select(i => rows.Max(r => r[i].Length)).ToArray();
            foreach (var r in rows)
                await stdout.WriteLineAsync(string.Join("  ", r.Select((cell, i) => cell.PadRight(widths[i]))).TrimEnd());
            foreach (var warning in await AdminGuard.BreakGlassWarnings(db, auth, ct)) await stderr.WriteLineAsync($"warning: {warning}");
            return Ok;
        }

        /// <summary>The new password from stdin, or the prompt (twice); null, after printing the reason, when there is none.</summary>
        private async Task<string?> ReadNewPasswordAsync(Command c)
        {
            if (c.Flags.Contains("--password-stdin"))
            {
                var text = await stdin.ReadToEndAsync(ct);
                if (text.EndsWith("\r\n", StringComparison.Ordinal)) return text[..^2];
                return text.EndsWith('\n') ? text[..^1] : text;
            }
            var first = prompt.ReadPassword("New password: ");
            var second = prompt.ReadPassword("Repeat password: ");
            if (first == second) return first;
            await stderr.WriteLineAsync("Passwords do not match");
            return null;
        }

        private int Refuse(string code)
        {
            stderr.WriteLine($"error: {code}: {(Messages.TryGetValue(code, out var message) ? message : "the operation was refused")}");
            return Refused;
        }

        private static bool IsValidUserName(string name) =>
            name.Length is >= 1 and <= LocalAuthSetup.MaxUserNameLength && name.All(ch => LocalAuthSetup.UserNameCharacters.Contains(ch));
    }

    /// <summary>
    /// Puts <c>via maldex-admin: &lt;command&gt;</c> in front of the details of every audit row saved in this process, so the rows
    /// <see cref="UserAdminService"/> writes (with the same text as when the API writes them) say where they came from.
    /// </summary>
    private sealed class AuditPrefixInterceptor(string prefix) : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Prefix(eventData.Context);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Prefix(eventData.Context);
            return ValueTask.FromResult(result);
        }

        private void Prefix(DbContext? context)
        {
            if (context is null) return;
            foreach (var entry in context.ChangeTracker.Entries<AuditLog>().Where(e => e.State == EntityState.Added))
                entry.Entity.Details = string.IsNullOrEmpty(entry.Entity.Details) ? prefix : $"{prefix}; {entry.Entity.Details}";
        }
    }
}
