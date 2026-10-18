# How to Run NoteService

This guide takes you from a fresh machine to a running API, and explains how to run the tests and the Docker setup.
Commands are written for Windows (PowerShell), but they work the same in any shell unless noted.

## 1. Requirements

### Required

| Software | Version | Why | Get it |
|---|---|---|---|
| .NET SDK | 10.0 or newer | Builds and runs the solution | https://dotnet.microsoft.com/download/dotnet/10.0 |
| SQL Server | 2019 or newer, **any one of** the options below | Application database | see below |
| EF Core command-line tool (`dotnet-ef`) | 10.x | Creates the database schema from the migrations | installed in step 2 |

Choose **one** way to get SQL Server:

- **SQL Server LocalDB** (simplest on Windows). It is included with Visual Studio (the "ASP.NET and web development" or "Data storage and processing" workload) and with SQL Server Express. The default instance is called `MSSQLLocalDB`.
- **SQL Server in Docker** (works on any OS). Needs Docker Desktop, see section 6.
- **Any existing SQL Server / SQL Server Express** you can reach. You will supply its connection string.

Check what you already have:

```powershell
dotnet --list-sdks          # should list a 10.0.x entry
sqllocaldb info             # should list MSSQLLocalDB (LocalDB users only)
```

### Optional

| Software | Needed for |
|---|---|
| Docker Desktop (with the Linux engine running) | Integration tests (they start a SQL Server container), `docker compose`, building the container image |
| Visual Studio 2022 17.14+ / 2026, VS Code with C# Dev Kit, or Rider | Editing and debugging; the command line is enough to run everything |
| Git | Cloning the repository |

You do **not** need an email server, cloud account or any other service. Emails (invites, password resets) are written to the application log in development.

## 2. One-time setup

Run these from the repository root (the folder that contains `NoteService.slnx`).

1. **Install or update the EF tool**

   ```powershell
   dotnet tool update --global dotnet-ef
   ```

   (This installs it if it is missing. Confirm with `dotnet ef --version`.)

2. **Restore and build**

   ```powershell
   dotnet build NoteService.slnx
   ```

   The build should finish with `0 Warning(s)` and `0 Error(s)`.

3. **Create the database**

   With LocalDB this needs no configuration; it creates a database named `NoteService`:

   ```powershell
   dotnet ef database update --project NoteService.Infrastructure
   ```

   To target a different SQL Server, set `NOTESERVICE_DB` to its connection string first:

   ```powershell
   $env:NOTESERVICE_DB = "Server=localhost,1433;Database=NoteService;User Id=sa;Password=<your password>;TrustServerCertificate=True"
   dotnet ef database update --project NoteService.Infrastructure
   ```

   > If you use a different server, also set the same connection string for the API in step 3.3 below.

## 3. Run the API

1. **Start it**

   ```powershell
   dotnet run --project NoteService.API
   ```

   The default launch profile uses the **Development** environment and listens on **http://localhost:5172**.
   Leave this window open. Press `Ctrl+C` to stop.

2. **Check that it works**

   Open these in a browser:

   | URL | What you get |
   |---|---|
   | http://localhost:5172/docs | Interactive API reference (Scalar). You can call every endpoint from here |
   | http://localhost:5172/openapi/v1.json | The OpenAPI document |
   | http://localhost:5172/health/live | Returns `Healthy` when the process is up |
   | http://localhost:5172/health/ready | Returns `Healthy` when the database is reachable |

   If `/health/ready` says `Unhealthy`, the database could not be reached; see Troubleshooting.

3. **Using a different database**

   The Development settings in `NoteService.API/appsettings.Development.json` point at LocalDB. To override that without editing files, set an environment variable before starting the API:

   ```powershell
   $env:ConnectionStrings__NoteDb = "Server=localhost,1433;Database=NoteService;User Id=sa;Password=<your password>;TrustServerCertificate=True"
   dotnet run --project NoteService.API
   ```

## 4. Try it out

In Development, e-mail confirmation is switched off, so you can register and sign in straight away.
Using the `/docs` page is the easiest route. To do it from the command line (PowerShell):

```powershell
$api = "http://localhost:5172/api/v1"
$json = @{ "Content-Type" = "application/json" }

# 1. Register and sign in
Invoke-RestMethod "$api/auth/register" -Method Post -Headers $json -Body (@{ email = "me@example.com"; password = "Sup3r-secret-pw!"; displayName = "Me" } | ConvertTo-Json)
$auth = Invoke-RestMethod "$api/auth/login" -Method Post -Headers $json -Body (@{ email = "me@example.com"; password = "Sup3r-secret-pw!" } | ConvertTo-Json)
$headers = @{ Authorization = "Bearer $($auth.accessToken)"; "Content-Type" = "application/json" }

# 2. Create a team (you become its owner) and a note
$team = Invoke-RestMethod "$api/teams" -Method Post -Headers $headers -Body (@{ name = "My Team" } | ConvertTo-Json)
$note = Invoke-RestMethod "$api/teams/$($team.id)/notes" -Method Post -Headers $headers -Body (@{ title = "Hello"; content = "# First note" } | ConvertTo-Json)

# 3. Read it back and search
Invoke-RestMethod "$api/teams/$($team.id)/notes" -Headers $headers
Invoke-RestMethod "$api/teams/$($team.id)/search?q=first" -Headers $headers
```

Things worth knowing when calling the API:

- Passwords need at least 10 characters.
- Access tokens last 15 minutes. Use `POST /auth/refresh` with the refresh token to get a new pair.
- Editing or deleting a note requires an `If-Match` header holding the note's current `ETag` (returned in the `ETag` response header and the `eTag` field). Without it you get `428`; with an outdated one you get `412`.
- Invite and password-reset links are not emailed in development. They appear in the API console output (look for lines starting `Email to ...`). Emails are delivered by a background job that runs every 60 seconds, so allow up to a minute; the link itself is logged at Debug level, which Development enables.

## 4a. Use the test UI (NoteService.Web)

`NoteService.Web` is a small browser app for adding notes and trying every API feature without writing requests by hand.
It has no build step and no extra requirements: it is plain HTML/JavaScript served by a tiny ASP.NET Core host.

1. Keep the API running (section 3). Open a **second** terminal in the repository root:

   ```powershell
   dotnet run --project NoteService.Web
   ```

2. Open **http://localhost:3000**. The pill at the top should say `API ready`. (The API's Development settings already allow this origin; if you run the UI on another port, add it to `Cors:AllowedOrigins`.)
3. Register an account, create a team, then explore the tabs:

   | Area | What you can test |
   |---|---|
   | Notes | Create, edit (Markdown), pin, move between folders, filter by folder/tag/pinned, sort, page through results, delete to trash |
   | Editor tabs | **History** (view and restore versions), **Sharing** (per-note Read/Edit overrides), **Attachments** (upload, download, delete), tags |
   | Concurrency | The *If-Match* dropdown beside **Save** lets you send a current, stale (expect `412`) or missing (expect `428`) ETag. Open the same note in two browser tabs to produce a real conflict |
   | Search / Trash | Full-text style search with snippets; restore trashed notes |
   | Members & invites | Invite people, change roles, remove members, leave a team, accept an invite by pasting its token |
   | Folders & tags | Nested folders (rename, move, delete) and team tags |
   | Team | Rename or delete the team |

4. **Request log** (bottom of the page) shows every call with its status, timing, ETag and response body (passwords and tokens are masked) so you can see exactly what the API returned.

Tips for testing permissions: buttons are intentionally never hidden by role, so a Viewer who clicks **Save** sees the server's `403`. To test two people, register a second account in a private/incognito window, invite its email from the first account, then paste the invite token (from the API console log) into **Accept invite**.
In Development, e-mail confirmation is off and links that would be emailed are logged by the API instead.

## 5. Run the tests

| Command | What it runs | Needs |
|---|---|---|
| `dotnet test NoteService.UnitTests` | ~460 fast unit tests | .NET SDK only |
| `dotnet test NoteService.IntegrationTests` | ~45 end-to-end tests against a real SQL Server | Docker Desktop running |
| `dotnet test NoteService.slnx` | Both | Docker Desktop running |

**Integration tests without Docker:** point them at an existing SQL Server (for example LocalDB). A uniquely named temporary database is created and dropped for the run.

```powershell
$env:NOTESERVICE_TEST_SQL = "Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True"
dotnet test NoteService.IntegrationTests
```

Code coverage for the unit tests:

```powershell
dotnet test NoteService.UnitTests --settings coverlet.runsettings --collect:"XPlat Code Coverage"
```

## 6. Run everything in Docker (optional)

This starts SQL Server and the API in containers. Docker Desktop must be running.

1. Set the two required secrets (use your own values; the JWT key must be at least 32 characters):

   ```powershell
   $env:SA_PASSWORD = "Choose-A-Strong-Password-1!"
   $env:JWT_SIGNING_KEY = "replace-with-a-long-random-string-of-32-plus-chars"
   ```

2. Start the database and create the schema:

   ```powershell
   docker compose up -d sql
   $env:NOTESERVICE_DB = "Server=localhost,1433;Database=NoteService;User Id=sa;Password=$env:SA_PASSWORD;TrustServerCertificate=True"
   dotnet ef database update --project NoteService.Infrastructure
   ```

   Wait about 20 seconds after `docker compose up` if the update reports it cannot connect; SQL Server needs a moment to start.

3. Build and start the API:

   ```powershell
   docker compose up -d --build api
   ```

4. Open http://localhost:8080/health/ready. It should say `Healthy`. The API is on port **8080** in this mode (the interactive `/docs` page is only enabled in the Development environment, so use the OpenAPI-compatible client of your choice or the commands in section 4 with `$api = "http://localhost:8080/api/v1"`).

5. Stop it with `docker compose down` (add `-v` to also delete the database and uploaded files).

## 7. Configuration reference

Settings live in `NoteService.API/appsettings.json` and can be overridden with environment variables (use `__` instead of `:`, for example `Jwt__SigningKey`).

| Setting | Default | Notes |
|---|---|---|
| `ConnectionStrings:NoteDb` | empty (LocalDB in Development) | SQL Server connection string |
| `Jwt:SigningKey` | none | **Required**, at least 32 characters. The API refuses to start without it. Development uses a built-in dev-only key; never reuse it in production |
| `App:PublicBaseUrl` | `https://notes.example.com` | Front-end address used in emailed links |
| `App:RequireConfirmedEmail` | `true` (`false` in Development) | Users must confirm their email before signing in |
| `Search:UseFullText` | `false` | Use SQL Server full-text search; requires the Full-Text feature |
| `BlobStorage:RootPath` | `App_Data/blobs` | Where attachments are stored |
| `Attachments:MaxBytes` | 10 MB | Upload limit |
| `Cors:AllowedOrigins` | none (Development allows `http://localhost:3000`) | Browser front-end origins |
| `Maintenance:Enabled` | `true` | Background jobs: trash purge after 30 days, token cleanup, email delivery, orphan file cleanup |
| `RateLimiting:*` | 300/min per user, 10/min on sign-in endpoints | Request limits |

## 8. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| `dotnet build` says the SDK is too old or `net10.0` is not supported | Install the .NET 10 SDK and reopen the terminal; check `dotnet --list-sdks` |
| `dotnet ef` is not recognised | Run `dotnet tool update --global dotnet-ef` and reopen the terminal |
| `dotnet ef database update` fails to connect to `(localdb)\MSSQLLocalDB` | Run `sqllocaldb start MSSQLLocalDB`. If LocalDB is not installed, use Docker or another SQL Server (set `NOTESERVICE_DB`) |
| API exits at startup with `The SigningKey field is required` | You are not running in Development. Use `dotnet run --project NoteService.API` (which uses the Development profile) or set `Jwt__SigningKey` |
| `/health/ready` is `Unhealthy` or requests return 500 on database access | The database does not exist or the connection string is wrong. Re-run step 2.3 and make sure the API uses the same connection string |
| The UI shows `API unreachable` or requests fail with a network/CORS message | The API is not running, the API box at the top has the wrong address, or the UI origin is not in `Cors:AllowedOrigins` (Development allows `http://localhost:3000`) |
| Port 5172 already in use | Run on another port: `dotnet run --project NoteService.API --urls http://localhost:5200` |
| Integration tests fail with a Docker error | Start Docker Desktop and wait until it shows the engine running, or set `NOTESERVICE_TEST_SQL` (section 5) |
| `429 Too Many Requests` | You hit a rate limit; wait a minute |
| `401` right after it worked | The 15-minute access token expired; sign in again or refresh it |
| Reset the local database | `dotnet ef database drop --force --project NoteService.Infrastructure` then `dotnet ef database update --project NoteService.Infrastructure` |
