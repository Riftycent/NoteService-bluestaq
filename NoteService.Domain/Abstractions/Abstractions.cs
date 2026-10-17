namespace NoteService.Domain.Abstractions;

/// <summary>The authenticated user of the current operation (null for anonymous or background work).</summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
}

/// <summary>Entities whose audit fields are maintained automatically on save.</summary>
public interface IAuditable
{
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    Guid CreatedBy { get; set; }
    Guid UpdatedBy { get; set; }
}

/// <summary>Entities protected by an optimistic-concurrency stamp that changes on every write (exposed as the ETag).</summary>
public interface IConcurrencyStamped
{
    Guid ConcurrencyStamp { get; set; }
}
