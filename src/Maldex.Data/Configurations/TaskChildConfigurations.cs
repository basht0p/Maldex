using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maldex.Data.Configurations;

public sealed class ProcessNodeConfiguration : IEntityTypeConfiguration<ProcessNode>
{
    public void Configure(EntityTypeBuilder<ProcessNode> b)
    {
        b.ToTable("ProcessNodes");
        b.HasKey(x => x.Id);
        b.Property(x => x.ImagePath);
        b.Property(x => x.CommandLine);
        b.Property(x => x.User).HasMaxLength(256);
        b.Property(x => x.Integrity).HasMaxLength(64);
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProcessNode>().WithMany().HasForeignKey(x => x.ParentNodeId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.TaskId);
    }
}

public sealed class BehaviorEventConfiguration : IEntityTypeConfiguration<BehaviorEvent>
{
    public void Configure(EntityTypeBuilder<BehaviorEvent> b)
    {
        b.ToTable("BehaviorEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Summary);
        b.Property(x => x.Data);
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ProcessNode>().WithMany().HasForeignKey(x => x.ProcessNodeId).OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => new { x.TaskId, x.At });
    }
}

public sealed class NetworkFlowConfiguration : IEntityTypeConfiguration<NetworkFlow>
{
    public void Configure(EntityTypeBuilder<NetworkFlow> b)
    {
        b.ToTable("NetworkFlows");
        b.HasKey(x => x.Id);
        b.Property(x => x.Proto).HasMaxLength(16).IsRequired();
        b.Property(x => x.Src).HasMaxLength(128).IsRequired();
        b.Property(x => x.Dst).HasMaxLength(256).IsRequired();
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TaskId);
    }
}

public sealed class HttpTransactionConfiguration : IEntityTypeConfiguration<HttpTransaction>
{
    public void Configure(EntityTypeBuilder<HttpTransaction> b)
    {
        b.ToTable("HttpTransactions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Method).HasMaxLength(16).IsRequired();
        b.Property(x => x.Url);
        b.Property(x => x.Host).HasMaxLength(512).IsRequired();
        b.Property(x => x.ReqHeaders);
        b.Property(x => x.RespHeaders);
        b.Property(x => x.BodyBlobKey).HasMaxLength(256);
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TaskId);
    }
}

public sealed class DetectionConfiguration : IEntityTypeConfiguration<Detection>
{
    public void Configure(EntityTypeBuilder<Detection> b)
    {
        b.ToTable("Detections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Rule).HasMaxLength(256).IsRequired();
        b.Property(x => x.Tags).HasMaxLength(1024).IsRequired();
        b.Property(x => x.Target);
        b.Property(x => x.Details);
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TaskId);
    }
}

public sealed class ArtifactConfiguration : IEntityTypeConfiguration<Artifact>
{
    public void Configure(EntityTypeBuilder<Artifact> b)
    {
        b.ToTable("Artifacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.BlobKey).HasMaxLength(256).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false).IsRequired();
        b.HasOne<AnalysisTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TaskId);
    }
}

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.ToTable("AuditLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(64).IsRequired();
        b.Property(x => x.Target).HasMaxLength(512).IsRequired();
        b.Property(x => x.Details);
        // Audit rows outlive users: deleting a user nulls the actor instead of deleting history.
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(x => x.At);
    }
}
