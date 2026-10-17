using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NoteService.Domain.Abstractions;

namespace NoteService.Infrastructure.Persistence;

/// <summary>Used by <c>dotnet ef</c> to create migrations and bundles.</summary>
public sealed class DesignTimeNoteDbContextFactory : IDesignTimeDbContextFactory<NoteDbContext>
{
    public NoteDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("NOTESERVICE_DB")
            ?? @"Server=(localdb)\MSSQLLocalDB;Database=NoteService;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<NoteDbContext>().UseSqlServer(connectionString).Options;
        return new NoteDbContext(options, new NullCurrentUser(), TimeProvider.System);
    }

    private sealed class NullCurrentUser : ICurrentUser
    {
        public Guid? UserId => null;
    }
}
