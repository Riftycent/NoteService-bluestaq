using NoteService.Domain.Abstractions;
using NoteService.Domain.Exceptions;

namespace NoteService.Domain.Entities;

public class Folder : IAuditable
{
    public const int NameMaxLength = 200;

    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid UpdatedBy { get; set; }

    public static Folder Create(Guid teamId, Guid? parentId, string name) =>
        new() { Id = Guid.CreateVersion7(), TeamId = teamId, ParentId = parentId, Name = Normalize(name) };

    public void Rename(string name) => Name = Normalize(name);

    private static string Normalize(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > NameMaxLength)
        {
            throw new ValidationFailedException("name", $"Name is required and must be at most {NameMaxLength} characters.");
        }

        return trimmed;
    }
}
