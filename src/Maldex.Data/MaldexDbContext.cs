using Maldex.Core.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Maldex.Data;

public class MaldexDbContext(DbContextOptions<MaldexDbContext> options) : IdentityUserContext<AppUser, Guid>(options)
{
    public DbSet<Org> Orgs => Set<Org>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();
    public DbSet<Sample> Samples => Set<Sample>();
    public DbSet<AnalysisTask> Tasks => Set<AnalysisTask>();
    public DbSet<TaskStatusHistory> TaskStatusHistory => Set<TaskStatusHistory>();
    public DbSet<ProcessNode> ProcessNodes => Set<ProcessNode>();
    public DbSet<BehaviorEvent> BehaviorEvents => Set<BehaviorEvent>();
    public DbSet<NetworkFlow> NetworkFlows => Set<NetworkFlow>();
    public DbSet<HttpTransaction> HttpTransactions => Set<HttpTransaction>();
    public DbSet<Detection> Detections => Set<Detection>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Template> Templates => Set<Template>();
    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();
    public DbSet<TemplateJob> TemplateJobs => Set<TemplateJob>();
    public DbSet<TemplateJobStep> TemplateJobSteps => Set<TemplateJobStep>();
    public DbSet<MediaFile> MediaFiles => Set<MediaFile>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every enum is stored as its name, max 32 chars.
        configurationBuilder.Properties<RunStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<NetworkMode>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<Verdict>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UserRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<UserKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<EventType>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<DetectionSource>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ArtifactKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TemplateVersionOrigin>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TemplateVersionStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TemplateJobKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<TemplateJobState>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<MediaKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<MediaSource>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<MediaStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Identity's tables get Maldex names (no AspNet* prefix); there are no roles or role-claims tables.
        modelBuilder.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims");
        modelBuilder.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins");
        modelBuilder.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MaldexDbContext).Assembly);
    }
}
