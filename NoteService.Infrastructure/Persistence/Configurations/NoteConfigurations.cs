using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NoteService.Domain.Entities;

namespace NoteService.Infrastructure.Persistence.Configurations;

public sealed class FolderConfiguration : IEntityTypeConfiguration<Folder>
{
    public void Configure(EntityTypeBuilder<Folder> builder)
    {
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.Name).HasMaxLength(Folder.NameMaxLength).IsRequired();
        builder.HasOne<Team>().WithMany().HasForeignKey(f => f.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Folder>().WithMany().HasForeignKey(f => f.ParentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(f => new { f.TeamId, f.ParentId });
    }
}

public sealed class NoteConfiguration : IEntityTypeConfiguration<Note>
{
    public void Configure(EntityTypeBuilder<Note> builder)
    {
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Id).ValueGeneratedNever();
        builder.Property(n => n.Title).HasMaxLength(Note.TitleMaxLength).IsRequired();
        builder.Property(n => n.ContentMarkdown).IsRequired();
        builder.Property(n => n.ContentText).IsRequired();
        builder.Property(n => n.ConcurrencyStamp).IsConcurrencyToken().ValueGeneratedNever();
        builder.Ignore(n => n.IsDeleted);

        builder.HasOne<Team>().WithMany().HasForeignKey(n => n.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Folder>().WithMany().HasForeignKey(n => n.FolderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(n => n.NoteTags).WithOne().HasForeignKey(nt => nt.NoteId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(n => new { n.TeamId, n.DeletedAt, n.UpdatedAt }).IsDescending(false, false, true);
        builder.HasIndex(n => new { n.TeamId, n.FolderId });
    }
}

public sealed class NoteVersionConfiguration : IEntityTypeConfiguration<NoteVersion>
{
    public void Configure(EntityTypeBuilder<NoteVersion> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Title).HasMaxLength(Note.TitleMaxLength).IsRequired();
        builder.Property(v => v.ContentMarkdown).IsRequired();
        builder.HasOne<Note>().WithMany().HasForeignKey(v => v.NoteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.NoteId, v.VersionNo }).IsUnique();
    }
}

public sealed class NoteShareConfiguration : IEntityTypeConfiguration<NoteShare>
{
    public void Configure(EntityTypeBuilder<NoteShare> builder)
    {
        builder.HasKey(s => new { s.NoteId, s.UserId });
        builder.HasOne<Note>().WithMany().HasForeignKey(s => s.NoteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(s => s.UserId);
    }
}

public sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
{
    public void Configure(EntityTypeBuilder<Tag> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Tag.NameMaxLength).IsRequired();
        builder.HasOne<Team>().WithMany().HasForeignKey(t => t.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.TeamId, t.Name }).IsUnique();
    }
}

public sealed class NoteTagConfiguration : IEntityTypeConfiguration<NoteTag>
{
    public void Configure(EntityTypeBuilder<NoteTag> builder)
    {
        builder.HasKey(nt => new { nt.NoteId, nt.TagId });
        // Restrict (NO ACTION): avoids a second cascade path from Team; tag deletion removes links explicitly.
        builder.HasOne(nt => nt.Tag).WithMany().HasForeignKey(nt => nt.TagId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(nt => nt.TagId);
    }
}

public sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.FileName).HasMaxLength(255).IsRequired();
        builder.Property(a => a.ContentType).HasMaxLength(150).IsRequired();
        builder.Property(a => a.StorageKey).HasMaxLength(200).IsRequired();
        builder.HasOne<Note>().WithMany().HasForeignKey(a => a.NoteId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => a.NoteId);
    }
}
