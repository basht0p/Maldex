namespace Maldex.Web.Auth;

/// <summary>The session's hard limit: a cookie is refused 7 days after sign-in however active the user is.</summary>
public static class SessionLifetime
{
    public static readonly TimeSpan AbsoluteCap = TimeSpan.FromDays(7);

    /// <summary>The sliding expiry of the session cookie.</summary>
    public static readonly TimeSpan SlidingExpiry = TimeSpan.FromHours(12);

    /// <summary>The fixed lifetime of the partial (unfinished sign-in) cookie.</summary>
    public static readonly TimeSpan PartialLifetime = TimeSpan.FromMinutes(10);

    public static bool IsBeyondAbsoluteCap(DateTimeOffset issuedAt, DateTimeOffset now) => now - issuedAt > AbsoluteCap;
}
