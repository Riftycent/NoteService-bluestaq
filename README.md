# NoteService

Backend for a note-taking service shared by small teams. ASP.NET Core 10 Web API (MVC controllers, REST), SQL Server, EF Core 10.

## Layout

```
NoteService.API                 host: controllers, auth, middleware, feature services (Features/*)
NoteService.Domain          entities, rules, exceptions (no EF / ASP.NET references)
NoteService.Infrastructure  EF Core DbContext + migrations, blob storage, email outbox, background jobs
NoteService.UnitTests         fast tests, no external services (in-process SQLite for data-access code)
NoteService.IntegrationTests  full API against a real SQL Server
NoteService.Web                 dependency-free browser UI for trying every feature (static files)
```

## Run locally

Prerequisites: .NET 10 SDK and SQL Server (LocalDB works: `(localdb)\MSSQLLocalDB`).

```bash
dotnet tool install --global dotnet-ef        # once
dotnet ef database update --project NoteService.Infrastructure
dotnet run --project NoteService.API
```

The Development profile uses the LocalDB connection string and a development-only JWT key from
`appsettings.Development.json`, skips email confirmation, and logs outgoing emails (invite links, reset links) instead of sending them.
API reference: `http://localhost:5172/docs` (Scalar) and `/openapi/v1.json`.

Set the connection string for another server with `ConnectionStrings__NoteDb`, or `NOTESERVICE_DB` when running `dotnet ef`.

## Configuration

| Setting | Purpose |
|---|---|
| `ConnectionStrings:NoteDb` | SQL Server connection string |
| `Jwt:SigningKey` | **Required**, at least 32 characters. Supply through an environment variable or secret store; the app refuses to start without it |
| `App:PublicBaseUrl` | Front-end URL used in emailed links |
| `App:RequireConfirmedEmail` | Require email confirmation before sign-in (default `true`) |
| `Search:UseFullText` | Use SQL Server full-text search (needs the Full-Text feature; the migration creates the catalog when available). Default is substring search |
| `BlobStorage:RootPath` | Where attachments are stored (swap `IBlobStore` for Azure Blob / S3 in production) |
| `Cors:AllowedOrigins` | Allowed browser origins |
| `Maintenance:*` | Background job interval, trash retention (30 days) |
| `RateLimiting:*` | Per-user and per-address request limits |

Email delivery is not implemented: `IEmailSender` has a logging implementation only. Register an SMTP/SendGrid implementation for production.

## Tests

```bash
dotnet test NoteService.UnitTests --settings coverlet.runsettings --collect:"XPlat Code Coverage"
dotnet test NoteService.IntegrationTests
```

Integration tests start a SQL Server container with Testcontainers, so Docker must be running. To use an existing SQL Server instead
(for example LocalDB), set `NOTESERVICE_TEST_SQL`; a uniquely named database is created and dropped for the run:

```bash
export NOTESERVICE_TEST_SQL='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True'
```

## Migrations

```bash
dotnet ef migrations add <Name> --project NoteService.Infrastructure --output-dir Persistence/Migrations
dotnet ef migrations bundle --project NoteService.Infrastructure --self-contained -o efbundle.exe   # deploy artifact
```

Production deployments should run the bundle; the app never migrates itself at startup.


## Assessment Summary

The design choices I spent the most time on were choosing the best Authentication for this type of app, How detailed of logging I should implement, and how rubust the note featureset should be. I wanted to add realtime note editing with something like SignalR. Where users could see another user editing a note in realtime.
If I had more time
A. I'd probably reduce the amount of unit tests I implemented. I think my 80% coverage is too high for this type of app
B. I'd add more robust features to the note taking actions. Like adding, editing, and deleting.
C. I would add a caching layer for static data
D. I'd add a real time notification system so I can allow users to use @mentions