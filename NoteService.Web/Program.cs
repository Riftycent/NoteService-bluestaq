// A deliberately tiny host for the static test UI in wwwroot. It has no server-side logic: the browser talks to
// NoteService.API directly (CORS for http://localhost:3000 is enabled by the API's Development settings).
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Links in emails (/invites/{token}, /confirm-email, /reset-password) are handled client-side.
app.MapFallbackToFile("index.html");

app.Run();
