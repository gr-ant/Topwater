// A web page with a terminal on the simulated machine, for anyone on your network.
//
//   dotnet run --project examples/WebShell                # http://<this machine>:8765
//   URL=http://127.0.0.1:8765 dotnet run --project examples/WebShell     # this machine only
//
// Each browser tab gets its own Shell. Nothing here touches the host: no host programs or MCP servers are
// registered, so visitors can only use the in-memory machine. Do not add RegisterHostCommand to this example;
// that would hand a real program to everyone who can open the page.
using System.Collections.Concurrent;
using Topwater;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
// IPv4 only by default: on many networks the IPv6 addresses are reachable from the internet
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("URL") ?? "http://0.0.0.0:8765");
var app = builder.Build();

const int MaxSessions = 100, MaxCommandChars = 20_000;
var sessions = new ConcurrentDictionary<string, Session>();
var page = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "index.html"));

app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

app.MapPost("/run", async (RunRequest request, CancellationToken aborted) =>
{
    if (request.Session is not { Length: >= 16 and <= 64 } id || request.Command is not { Length: <= MaxCommandChars } command)
        return Results.BadRequest();
    if (!sessions.TryGetValue(id, out var session))
    {
        // shortcut: when full, the session idle longest is dropped; use real expiry if this ever runs unattended
        if (sessions.Count >= MaxSessions && sessions.MinBy(s => s.Value.LastUsed) is { Key: { } oldest }) sessions.TryRemove(oldest, out _);
        session = sessions.GetOrAdd(id, _ => new Session());
    }
    await session.Gate.WaitAsync(aborted);   // a Shell runs one command at a time
    try
    {
        session.LastUsed = DateTime.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        string output;
        try { output = command.Trim().Length == 0 ? "" : await BashTool.ExecuteAsync(session.Shell, command, timeout.Token); }
        catch (OperationCanceledException) { output = "[stopped after 20 seconds]\n"; }
        var home = session.Shell.Environment.GetValueOrDefault("HOME", "/home/agent");
        var cwd = session.Shell.Cwd;
        if (cwd == home || cwd.StartsWith(home + "/")) cwd = "~" + cwd[home.Length..];
        return Results.Json(new { output, prompt = $"agent@topwater:{cwd}$ " });
    }
    finally { session.Gate.Release(); }
});

Console.WriteLine($"Topwater web shell listening on {string.Join(", ", app.Urls.DefaultIfEmpty("http://0.0.0.0:8765"))}");
app.Run();

record RunRequest(string? Session, string? Command);

sealed class Session
{
    public Shell Shell { get; } = new();
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public DateTime LastUsed { get; set; } = DateTime.UtcNow;
}
