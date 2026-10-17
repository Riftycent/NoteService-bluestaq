using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace NoteService.API.Common;

/// <summary>Shared conventions: JSON, API v1, authenticated by default (opt out with <c>[AllowAnonymous]</c>).</summary>
[ApiController]
[ApiVersion("1.0")]
[Authorize]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected void SetETag(string etag) => Response.Headers.ETag = etag;
}

public static class ApiRoutes
{
    /// <summary>Route prefix shared by all controllers.</summary>
    public const string Base = "api/v{version:apiVersion}";
}
