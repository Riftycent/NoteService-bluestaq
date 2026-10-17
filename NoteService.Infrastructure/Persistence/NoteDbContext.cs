using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NoteService.Domain.Abstractions;
using NoteService.Domain.Entities;

namespace NoteService.Infrastructure.Persistence;

public class NoteDbContext(DbContextOptions<NoteDbContext> options, ICurrentUser currentUser, TimeProvider time)
    : IdentityUserContext<ApplicationUser, Guid>(options)
{
    /// <summary>Restricts tenant-owned rows to the teams the current user belongs to.</summary>
    public const string TenantFilter = "Tenant";

    /// <summary>Hides soft-deleted notes. Ignore only this filter to reach the trash.</summary>
    public const string SoftDeleteFilter = "SoftDelete";

    public DbSet<Team> Teams => Set<Team>();
    public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
    public DbSet<TeamInvite> TeamInvites => Set<TeamInvite>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<NoteVersion> NoteVersions => Set<NoteVersion>();
    public DbSet<NoteShare> NoteShares => Set<NoteShare>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<NoteTag> NoteTags => Set<NoteTag>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();

    // Referenced by the query filters; EF re-evaluates it per context instance.
    private Guid? CurrentUserId => currentUser.UserId;

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(NoteDbContext).Assembly);

        builder.Entity<Team>().HasQueryFilter(TenantFilter,
            t => TeamMembers.Any(m => m.TeamId == t.Id && m.UserId == CurrentUserId));
        builder.Entity<Folder>().HasQueryFilter(TenantFilter,
            f => TeamMembers.Any(m => m.TeamId == f.TeamId && m.UserId == CurrentUserId));
        builder.Entity<Tag>().HasQueryFilter(TenantFilter,
            t => TeamMembers.Any(m => m.TeamId == t.TeamId && m.UserId == CurrentUserId));
        builder.Entity<Note>()
            .HasQueryFilter(TenantFilter, n => TeamMembers.Any(m => m.TeamId == n.TeamId && m.UserId == CurrentUserId))
            .HasQueryFilter(SoftDeleteFilter, n => n.DeletedAt == null);

        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            ApplySqliteDateTimeOffsetConversion(builder);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void ApplyAuditFields()
    {
        var now = time.GetUtcNow();
        var userId = currentUser.UserId ?? Guid.Empty;

        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added)
                {
                    auditable.CreatedAt = now;
                    auditable.CreatedBy = userId;
                }

                auditable.UpdatedAt = now;
                auditable.UpdatedBy = userId;
            }

            if (entry.Entity is IConcurrencyStamped stamped)
            {
                stamped.ConcurrencyStamp = Guid.NewGuid();
            }
        }
    }

    /// <summary>SQLite (used by unit tests) cannot order or compare DateTimeOffset, so store UTC ticks instead.</summary>
    private static void ApplySqliteDateTimeOffsetConversion(ModelBuilder builder)
    {
        var converter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        foreach (var property in builder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.ClrType == typeof(DateTimeOffset) || property.ClrType == typeof(DateTimeOffset?))
            {
                property.SetValueConverter(converter);
            }
        }
    }
}
