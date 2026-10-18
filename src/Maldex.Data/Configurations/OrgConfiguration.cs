using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maldex.Data.Configurations;

public sealed class OrgConfiguration : IEntityTypeConfiguration<Org>
{
    public void Configure(EntityTypeBuilder<Org> b)
    {
        b.ToTable("Orgs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.EntraObjectId).HasMaxLength(64).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.EntraObjectId).IsUnique();
    }
}

public sealed class SampleConfiguration : IEntityTypeConfiguration<Sample>
{
    public void Configure(EntityTypeBuilder<Sample> b)
    {
        b.ToTable("Samples");
        b.HasKey(x => x.Sha256);
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.Sha1).HasMaxLength(40).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.Md5).HasMaxLength(32).IsFixedLength().IsUnicode(false).IsRequired();
        b.Property(x => x.FileType).HasMaxLength(128);
        b.Property(x => x.Mime).HasMaxLength(128);
        b.Property(x => x.OriginalName).HasMaxLength(260);
        b.Property(x => x.BlobKey).HasMaxLength(256).IsRequired();
    }
}
