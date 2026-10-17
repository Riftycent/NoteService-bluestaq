using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace NoteService.UnitTests.TestSupport;

/// <summary>
/// <see cref="DefaultHttpContext"/> ignores <c>Response.OnStarting</c> callbacks; this feature records them
/// so a test can run them the way the server does just before the response is sent.
/// </summary>
public sealed class CapturingResponseFeature : HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

    public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

    public async Task FireOnStartingAsync()
    {
        foreach (var (callback, state) in _callbacks)
        {
            await callback(state);
        }
    }

    public static DefaultHttpContext CreateContext(out CapturingResponseFeature feature)
    {
        feature = new CapturingResponseFeature();
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(feature);
        return context;
    }
}
