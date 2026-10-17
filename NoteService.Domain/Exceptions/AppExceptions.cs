namespace NoteService.Domain.Exceptions;

/// <summary>Base type for expected, client-facing failures. The API maps each subtype to an HTTP status.</summary>
public abstract class AppException(string message) : Exception(message);

public sealed class NotFoundException(string message = "The requested resource was not found.") : AppException(message);

public sealed class ForbiddenException(string message = "You do not have permission to perform this action.") : AppException(message);

public sealed class ConflictException(string message) : AppException(message);

public sealed class UnauthorizedAppException(string message = "Authentication failed.") : AppException(message);

public sealed class PreconditionRequiredException(string message = "An If-Match header is required for this request.") : AppException(message);

public sealed class PreconditionFailedException(string message = "The resource was modified since it was last read.", string? currentETag = null) : AppException(message)
{
    public string? CurrentETag { get; } = currentETag;
}

public sealed class ValidationFailedException : AppException
{
    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public ValidationFailedException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    public ValidationFailedException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }
}
