using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NSubstitute;
using NoteService.API.Authorization;
using NoteService.API.Common;
using NoteService.API.Features.Search;
using NoteService.Domain.Exceptions;
using NoteService.UnitTests.TestSupport;

namespace NoteService.UnitTests.Api.Search;

public class SearchServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private (SearchService Service, NoteService.Infrastructure.Persistence.NoteDbContext Context) Create()
    {
        var context = _db.NewContext();
        return (new SearchService(context, new TeamAccess(context, _db.CurrentUser), Options.Create(new SearchOptions())), context);
    }

    private (Guid TeamId, Guid UserId) Seed()
    {
        var user = _db.AddUser();
        var team = _db.AddTeam(user.Id);
        _db.CurrentUser.UserId = user.Id;
        return (team.Id, user.Id);
    }

    [Fact]
    public async Task SearchAsync_MatchesTitleOrBody()
    {
        var (teamId, userId) = Seed();
        _db.AddNote(teamId, userId, "Quarterly roadmap", "nothing relevant");
        _db.AddNote(teamId, userId, "Meeting", "discuss the roadmap with the team");
        _db.AddNote(teamId, userId, "Groceries", "milk and eggs");
        var (service, context) = Create();
        await using var _ = context;

        var results = await service.SearchAsync(teamId, "roadmap", 20, default);

        Assert.Equal(["Meeting", "Quarterly roadmap"], results.Select(r => r.Title).Order());
    }

    [Fact]
    public async Task SearchAsync_MultipleTerms_AreCombinedWithAnd()
    {
        var (teamId, userId) = Seed();
        _db.AddNote(teamId, userId, "Both", "alpha and beta");
        _db.AddNote(teamId, userId, "OnlyAlpha", "alpha only");
        var (service, context) = Create();
        await using var _ = context;

        var results = await service.SearchAsync(teamId, "alpha beta", 20, default);

        Assert.Equal(["Both"], results.Select(r => r.Title));
    }

    [Fact]
    public async Task SearchAsync_ReturnsSnippetAroundMatch()
    {
        var (teamId, userId) = Seed();
        _db.AddNote(teamId, userId, "Long", new string('x', 400) + " needle " + new string('y', 400));
        var (service, context) = Create();
        await using var _ = context;

        var result = (await service.SearchAsync(teamId, "needle", 20, default)).Single();

        Assert.Contains("needle", result.Snippet);
        Assert.True(result.Snippet.Length < 250);
    }

    [Fact]
    public async Task SearchAsync_ExcludesTrashAndOtherTeams()
    {
        var (teamId, userId) = Seed();
        var otherTeam = _db.AddTeam(userId, "Other");
        _db.AddNote(teamId, userId, "trashed topic", deleted: true);
        _db.AddNote(otherTeam.Id, userId, "topic elsewhere");
        _db.AddNote(teamId, userId, "topic here");
        var (service, context) = Create();
        await using var _ = context;

        var results = await service.SearchAsync(teamId, "topic", 20, default);

        Assert.Equal(["topic here"], results.Select(r => r.Title));
    }

    [Fact]
    public async Task SearchAsync_WildcardCharactersAreTreatedLiterally()
    {
        var (teamId, userId) = Seed();
        _db.AddNote(teamId, userId, "percent", "100% sure");
        _db.AddNote(teamId, userId, "other", "completely different");
        var (service, context) = Create();
        await using var _ = context;

        var results = await service.SearchAsync(teamId, "100%", 20, default);

        Assert.Equal(["percent"], results.Select(r => r.Title));
    }

    [Fact]
    public async Task SearchAsync_LimitCapsResults()
    {
        var (teamId, userId) = Seed();
        for (var i = 0; i < 5; i++)
        {
            _db.AddNote(teamId, userId, $"match {i}");
        }

        var (service, context) = Create();
        await using var _ = context;

        Assert.Equal(2, (await service.SearchAsync(teamId, "match", 2, default)).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    public async Task SearchAsync_TooShortQuery_ThrowsValidationError(string query)
    {
        var (teamId, _) = Seed();
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<ValidationFailedException>(() => service.SearchAsync(teamId, query, 20, default));
    }

    [Fact]
    public async Task SearchAsync_NonMember_ThrowsNotFound()
    {
        var (teamId, _) = Seed();
        _db.CurrentUser.UserId = _db.AddUser().Id;
        var (service, context) = Create();
        await using var _ = context;

        await Assert.ThrowsAsync<NotFoundException>(() => service.SearchAsync(teamId, "anything", 20, default));
    }

    [Fact]
    public void ParseTerms_SplitsDeduplicatesAndCapsTerms()
    {
        var terms = SearchService.ParseTerms("  Alpha alpha  BETA gamma delta eps zeta eta theta iota ");

        Assert.Equal(["Alpha", "BETA", "gamma", "delta", "eps", "zeta", "eta", "theta"], terms);
    }

    [Fact]
    public void ParseTerms_TooLong_Throws() =>
        Assert.Throws<ValidationFailedException>(() => SearchService.ParseTerms(new string('a', SearchService.MaxQueryLength + 1)));

    [Fact]
    public void ParseTerms_Null_Throws() => Assert.Throws<ValidationFailedException>(() => SearchService.ParseTerms(null));
}

public class SearchControllerTests
{
    [Fact]
    public async Task Search_ReturnsServiceResults()
    {
        var service = Substitute.For<ISearchService>();
        var teamId = Guid.NewGuid();
        IReadOnlyList<SearchResultDto> results = [new SearchResultDto(Guid.NewGuid(), "T", "snippet", null, DateTimeOffset.UtcNow)];
        service.SearchAsync(teamId, "term", 5, Arg.Any<CancellationToken>()).Returns(results);

        var result = await new SearchController(service).Search(teamId, "term", 5, default);

        Assert.Equal(results, Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
