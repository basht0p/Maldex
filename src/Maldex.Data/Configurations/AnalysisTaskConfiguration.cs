using System.Text.Json;
using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maldex.Data.Configurations;

public sealed class AnalysisTaskConfiguration : IEntityTypeConfiguration<AnalysisTask>
{
    public void Configure(EntityTypeBuilder<AnalysisTask> b)
    {
        b.ToTable("Tasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.SampleSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.OriginalFileName).HasMaxLength(260);
        b.Property(x => x.Url);
        b.Property(x => x.Image).HasMaxLength(64).IsRequired();
        b.Property(x => x.ExitCountry).HasMaxLength(8);
        b.Property(x => x.FailureReason);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.Property(x => x.ProcessingWarnings)
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, c) => ReferenceEquals(a, c) || (a != null && c != null && a.SequenceEqual(c)),
                    v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
                    v => v.ToList()))
            .HasColumnType("nvarchar(max)");

        b.HasOne(x => x.Org).WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Sample).WithMany().HasForeignKey(x => x.SampleSha256).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.SubmittedBy).WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.TemplateVersionId).OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.StatusHistory).WithOne().HasForeignKey(h => h.TaskId).OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => new { x.Status, x.Interactive, x.CreatedAt });
    }
}

public sealed class TaskStatusHistoryConfiguration : IEntityTypeConfiguration<TaskStatusHistory>
{
    public void Configure(EntityTypeBuilder<TaskStatusHistory> b)
    {
        b.ToTable("TaskStatusHistory");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason);
        b.HasIndex(x => x.TaskId);
    }
}
