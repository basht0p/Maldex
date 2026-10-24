using Maldex.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Maldex.Data.Configurations;

public sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> b)
    {
        b.ToTable("Templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasMaxLength(64).ValueGeneratedNever();
        b.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(1000);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.CurrentVersionId).OnDelete(DeleteBehavior.Restrict);

        // Exactly one default template.
        b.HasIndex(x => x.IsDefault).IsUnique().HasFilter("[IsDefault] = 1").HasDatabaseName("IX_Templates_IsDefault");
    }
}

public sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> b)
    {
        b.ToTable("TemplateVersions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TemplateId).HasMaxLength(64).IsRequired();
        b.Property(x => x.FileStem).HasMaxLength(64).IsRequired();
        b.Property(x => x.WindowsEdition).HasMaxLength(100);
        b.Property(x => x.WindowsBuild).HasMaxLength(32);
        b.Property(x => x.AgentVersion).HasMaxLength(32);
        b.Property(x => x.SysmonConfigSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.MitmCaSha256).HasMaxLength(64).IsFixedLength().IsUnicode(false);
        b.Property(x => x.SourceMediaIds).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Note).HasMaxLength(1000);

        b.HasOne<Template>().WithMany().HasForeignKey(x => x.TemplateId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<TemplateVersion>().WithMany().HasForeignKey(x => x.ParentVersionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.SealedByUserId).OnDelete(DeleteBehavior.Restrict);

        b.HasIndex(x => new { x.TemplateId, x.Number }).IsUnique();
    }
}
