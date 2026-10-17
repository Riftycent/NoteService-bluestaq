using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NoteService.Domain.Exceptions;

namespace NoteService.API.Common;

/// <summary>Maps expected application exceptions to RFC 9457 problem responses; anything else falls through to the default 500 handling.</summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationFailedException validation:
                problem = new ValidationProblemDetails(validation.Errors.ToDictionary(e => e.Key, e => e.Value))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred."
                };
                break;
            case NotFoundException:
                problem = Create(StatusCodes.Status404NotFound, "Not found", exception.Message);
                break;
            case ForbiddenException:
                problem = Create(StatusCodes.Status403Forbidden, "Forbidden", exception.Message);
                break;
            case ConflictException:
                problem = Create(StatusCodes.Status409Conflict, "Conflict", exception.Message);
                break;
            case UnauthorizedAppException:
                problem = Create(StatusCodes.Status401Unauthorized, "Unauthorized", exception.Message);
                break;
            case PreconditionRequiredException:
                problem = Create(StatusCodes.Status428PreconditionRequired, "Precondition required", exception.Message);
                break;
            case PreconditionFailedException failed:
                problem = Create(StatusCodes.Status412PreconditionFailed, "Precondition failed", exception.Message);
                if (failed.CurrentETag is not null)
                {
                    problem.Extensions["currentETag"] = failed.CurrentETag;
                }

                break;
            case BadHttpRequestException bad:
                problem = Create(bad.StatusCode, "Bad request", bad.Message);
                break;
            default:
                return false;
        }

        httpContext.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }

    private static ProblemDetails Create(int status, string title, string detail) =>
        new() { Status = status, Title = title, Detail = detail };
}
