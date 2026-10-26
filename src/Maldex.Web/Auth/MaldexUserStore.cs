using Maldex.Core.Domain;
using Maldex.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Maldex.Web.Auth;

/// <summary>The Identity user store over <see cref="MaldexDbContext"/> (users, claims, logins and tokens; no role tables).</summary>
public class MaldexUserStore(MaldexDbContext context, IdentityErrorDescriber? describer = null)
    : UserOnlyStore<AppUser, MaldexDbContext, Guid>(context, describer);
