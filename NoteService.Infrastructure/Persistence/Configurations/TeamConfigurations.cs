using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NoteService.Domain.Entities;

namespace NoteService.Infrastructure.Persistence.Configurations;

public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder) =>
        builder.Property(u => u.DisplayName).HasMaxLength(ApplicationUser.DisplayNameMaxLength).IsRequired();
}

public sealed class TeamConfiguration : IEntityTypeConfiguration<Team>
{
    public void Configure(EntityTypeBuilder<Team> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).HasMaxLength(Team.NameMaxLength).IsRequired();
    }
}

public sealed class TeamMemberConfiguration : IEntityTypeConfiguration<TeamMember>
{
    public void Configure(EntityTypeBuilder<TeamMember> builder)
    {
        builder.HasKey(m => new { m.TeamId, m.UserId });
        builder.HasOne<Team>().WithMany().HasForeignKey(m => m.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(m => m.UserId);
    }
}

public sealed class TeamInviteConfiguration : IEntityTypeConfiguration<TeamInvite>
{
    public void Configure(EntityTypeBuilder<TeamInvite> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Email).HasMaxLength(256).IsRequired();
        builder.Property(i => i.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasOne<Team>().WithMany().HasForeignKey(i => i.TeamId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(i => i.TokenHash).IsUnique();
        builder.HasIndex(i => new { i.TeamId, i.Email });
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.Ignore(t => t.IsRevoked);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);
    }
}

public sealed class OutboxEmailConfiguration : IEntityTypeConfiguration<OutboxEmail>
{
    public void Configure(EntityTypeBuilder<OutboxEmail> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.To).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Body).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(500);
        builder.HasIndex(e => e.SentAt);
    }
}
