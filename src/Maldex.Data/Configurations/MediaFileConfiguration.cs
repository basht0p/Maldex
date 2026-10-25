using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maldex.Data.Configurations;

public sealed class MediaFileConfiguration : IEntityTypeConfiguration<MediaFile>
{
    public void Configure(EntityTypeBuilder<MediaFile> b)
    {
        b.ToTable("MediaFiles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.SourceUrl).HasMaxLength(2048);
        b.Property(x => x.Contents).HasColumnType("nvarchar(max)");
        b.Property(x => x.FailureReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.AddedByUserId).OnDelete(DeleteBehavior.Restrict);

        // One ready file per content hash; failed or deleted duplicates may remain.
        b.HasIndex(x => x.Sha256).IsUnique().HasFilter("[Status] = 'Ready'").HasDatabaseName("IX_MediaFiles_Sha256_Ready");
    }
}
