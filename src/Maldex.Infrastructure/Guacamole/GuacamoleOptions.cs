namespace Maldex.Infrastructure.Guacamole;

public sealed class GuacamoleOptions
{
    public const string SectionName = "Maldex:Guacamole";

    /// <summary>Where guacd listens. The container binds loopback only.</summary>
    public string GuacdHost { get; set; } = "127.0.0.1";

    public int GuacdPort { get; set; } = 4822;

    /// <summary>How long an issued tunnel token stays valid.</summary>
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often an open tunnel re-checks that its task is still Running.</summary>
    public TimeSpan StatusPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often an open template-job console marks the job as in use. A Configuring job is discarded after
    /// <c>ConfigureIdleTimeout</c> (2 h) without a console, counted from the last mark, so this must stay far below that.
    /// </summary>
    public TimeSpan ConsoleKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(60);
}
