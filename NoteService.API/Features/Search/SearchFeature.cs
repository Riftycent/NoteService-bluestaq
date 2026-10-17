using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.Domain.Enums;
using NoteService.Domain.Exceptions;
using NoteService.Infrastructure.Persistence;

namespace NoteService.API.Features.Search;

public sealed record SearchResultDto(Guid NoteId, string Title, string Snippet, Guid? FolderId, DateTimeOffset UpdatedAt);

public interface ISearchService
{
    Task<IReadOnlyList<SearchResultDto>> SearchAsync(Guid teamId, string query, int limit, CancellationToken ct);
}

public sealed class SearchService(NoteDbContext db, ITeamAccess teamAccess, IOptions<SearchOptions> options) : ISearchService
{
    public const int MaxQueryLength = 200;
    public const int MaxTerms = 8;

    public async Task<IReadOnlyList<SearchResultDto>> SearchAsync(Guid teamId, string query, int limit, CancellationToken ct)
    {
        await teamAccess.RequireRoleAsync(teamId, ct);

        var terms = ParseTerms(query);
        var notes = db.Notes.AsNoTracking().Where(n => n.TeamId == teamId);

        if (options.Value.UseFullText)
        {
            var phrase = string.Join(' ', terms);
            notes = notes.Where(n => EF.Functions.FreeText(n.Title, phrase) || EF.Functions.FreeText(n.ContentText, phrase));
        }
        else
        {
            foreach (var term in terms)
            {
                var t = term;
                notes = notes.Where(n => n.Title.Contains(t) || n.ContentText.Contains(t));
            }
        }

        var rows = await notes
            .OrderByDescending(n => n.UpdatedAt)
            .ThenByDescending(n => n.Id)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(n => new { n.Id, n.Title, n.ContentText, n.FolderId, n.UpdatedAt })
            .ToListAsync(ct);

        return rows.Select(r => new SearchResultDto(r.Id, r.Title, SnippetBuilder.Build(r.ContentText, terms), r.FolderId, r.UpdatedAt)).ToList();
    }

    /// <summary>Splits the query into distinct, trimmed terms and rejects empty or oversized input.</summary>
    internal static IReadOnlyList<string> ParseTerms(string? query)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length < 2 || trimmed.Length > MaxQueryLength)
        {
            throw new ValidationFailedException("q", $"The search text must be between 2 and {MaxQueryLength} characters.");
        }

        return trimmed
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTerms)
            .ToList();
    }
}

[Route($"{ApiRoutes.Base}/teams/{{teamId:guid}}/search")]
[TeamAuthorize(TeamRole.Viewer)]
public sealed class SearchController(ISearchService search) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SearchResultDto>>> Search(
        Guid teamId, [FromQuery] string q, [FromQuery] int limit = 20, CancellationToken ct = default) =>
        Ok(await search.SearchAsync(teamId, q, limit, ct));
}
