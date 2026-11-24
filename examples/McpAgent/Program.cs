// End-to-end check: a real model triages an issue tracker that it can only reach through an MCP server mounted
// as the `tracker` command. The model is given one tool (bash) and no tool definitions for the tracker.
//
//   GEMINI_API_KEY=... dotnet run --project examples/McpAgent
using System.ComponentModel;
using System.IO.Pipelines;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topwater;
using Topwater.Mcp;

var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set GEMINI_API_KEY"); return 2; }

// ---------- a small issue tracker behind an MCP server ----------
var issues = new List<Issue>
{
    new(1, "Login page times out", "open", ["bug"], null),
    new(2, "Add dark mode", "open", ["feature"], null),
    new(3, "CSV export drops last row", "open", ["bug", "data"], "sam"),
    new(4, "Crash on empty upload", "open", ["bug"], null),
    new(5, "Typo on pricing page", "closed", ["bug"], null),
    new(6, "Webhook retries never stop", "open", ["bug", "api"], null),
    new(7, "Login times out (duplicate of #1)", "open", ["bug"], null),
    new(8, "Update onboarding copy", "open", ["docs"], null),
};
var comments = new List<(int id, string body)>();
var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
Issue Find(int id) => issues.FirstOrDefault(i => i.Id == id) ?? throw new ArgumentException($"no issue with id {id}");

Pipe toServer = new(), toClient = new();
var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), new McpServerOptions
{
    ServerInfo = new Implementation { Name = "tracker", Version = "1.0", Description = "The team's issue tracker" },
    ToolCollection =
    [
        McpServerTool.Create(([Description("open or closed")] string? state = null, [Description("Only issues with this label")] string? label = null) =>
                JsonSerializer.Serialize(issues.Where(i => (state is null || i.State == state) && (label is null || i.Labels.Contains(label))), json),
            new() { Name = "list_issues", Description = "List issues, optionally filtered by state and label" }),
        McpServerTool.Create(([Description("Issue id")] int id) => JsonSerializer.Serialize(Find(id), json),
            new() { Name = "get_issue", Description = "Show one issue" }),
        McpServerTool.Create(([Description("Issue id")] int id, [Description("Username to assign")] string? assignee = null, [Description("open or closed")] string? state = null) =>
            {
                var issue = Find(id);
                if (state is not null and not ("open" or "closed")) throw new ArgumentException("state must be open or closed");
                issues[issues.IndexOf(issue)] = issue = issue with { Assignee = assignee ?? issue.Assignee, State = state ?? issue.State };
                return JsonSerializer.Serialize(issue, json);
            },
            new() { Name = "update_issue", Description = "Change an issue's assignee or state" }),
        McpServerTool.Create(([Description("Issue id")] int id, [Description("Comment text")] string body) =>
            {
                Find(id); comments.Add((id, body));
                return "comment added";
            },
            new() { Name = "add_comment", Description = "Add a comment to an issue" }),
        McpServerTool.Create(() => """["dana","sam","lee"]""", new() { Name = "list_users", Description = "List usernames that can be assigned" }),
    ],
});
using var stop = new CancellationTokenSource();
_ = server.RunAsync(stop.Token);
await using var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()));

var shell = new Shell();
await shell.MountMcpAsync("tracker", client);

// ---------- the agent loop ----------
const string Task = """
    The team's issue tracker is available as the `tracker` command. Please triage it:
    1. Issue 7 is a duplicate. Close it.
    2. Assign every remaining open issue labelled "bug" that has no assignee to dana, and add the comment "Triaged" to each.
    3. Write the ids you assigned to ~/triaged.txt, one per line, lowest first.
    4. Tell me how many open issues are left unassigned afterwards.
    """;
var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
http.DefaultRequestHeaders.Add("x-goog-api-key", key);
var tools = JsonNode.Parse($$"""[{"functionDeclarations":[{"name":"{{BashTool.Name}}","description":{{JsonSerializer.Serialize(BashTool.Description)}},"parameters":{{BashTool.InputSchemaJson}}}]}]""");
var contents = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = Task } } } };
string answer = "(turn limit reached)";
int commands = 0, failed = 0;
for (int turn = 0; turn < 30; turn++)
{
    var request = new JsonObject
    {
        ["systemInstruction"] = JsonNode.Parse("""{"parts":[{"text":"You are an autonomous agent working through a bash tool. Use the tool to inspect and act; do not ask the user questions. When finished, reply with a short final answer."}]}"""),
        ["contents"] = contents.DeepClone(), ["tools"] = tools!.DeepClone(),
    };
    var response = await http.PostAsJsonAsync("https://generativelanguage.googleapis.com/v1beta/models/gemini-flash-latest:generateContent", request);
    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync());
    if (!response.IsSuccessStatusCode) { Console.Error.WriteLine($"model call failed: {(int)response.StatusCode} {body?["error"]?["message"]}"); return 1; }
    var content = body?["candidates"]?[0]?["content"]?.DeepClone() ?? JsonNode.Parse("""{"role":"model","parts":[]}""")!;
    contents.Add(content);   // echoed back verbatim, which keeps the model's thought signatures
    var parts = content["parts"]?.AsArray() ?? [];
    var calls = parts.Where(p => p?["functionCall"] is not null).ToList();
    if (calls.Count == 0)
    {
        answer = string.Concat(parts.Where(p => p?["thought"] is null).Select(p => p?["text"]?.GetValue<string>())).Trim();
        break;
    }
    var replies = new JsonArray();
    foreach (var call in calls)
    {
        string command = call!["functionCall"]!["args"]?["command"]?.GetValue<string>() ?? "";
        var result = await shell.RunAsync(command);
        commands++;
        if (result.ExitCode != 0) failed++;
        Console.WriteLine($"  $ {command.Trim().Split('\n')[0]}\n    -> exit {result.ExitCode}{(result.Stderr.Trim().Length > 0 ? " | " + result.Stderr.Trim().Split('\n')[0] : "")}");
        replies.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = call["functionCall"]!["name"]!.GetValue<string>(), ["response"] = new JsonObject { ["output"] = result.ToToolOutput() } } });
    }
    contents.Add(new JsonObject { ["role"] = "user", ["parts"] = replies });
}
stop.Cancel();

// ---------- checks against the tracker's real state ----------
int[] expected = [1, 4, 6];
string triaged = shell.Fs.IsFile("/home/agent/triaged.txt") ? shell.Fs.ReadAllText("/home/agent/triaged.txt") : "";
var checks = new (string label, bool ok)[]
{
    ("issue 7 is closed", Find(7).State == "closed"),
    ("issues 1, 4 and 6 are assigned to dana", expected.All(id => Find(id).Assignee == "dana")),
    ("no other issue changed", Find(2).Assignee is null && Find(8).Assignee is null && Find(3).Assignee == "sam" && Find(5).Assignee is null && Find(7).Assignee is null),
    ("each assigned issue got the comment, and nothing else did", comments.Select(c => c.id).Order().SequenceEqual(expected) && comments.All(c => c.body.Trim().Trim('"') == "Triaged")),
    ("~/triaged.txt lists 1, 4, 6", triaged.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).SequenceEqual(["1", "4", "6"])),
    ("answer says 2 open issues are unassigned", System.Text.RegularExpressions.Regex.IsMatch(answer, @"\b(2|two)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)),
};
Console.WriteLine($"\n=== MCP triage: {commands} commands, {failed} non-zero exits\n{answer}\n");
foreach (var (label, ok) in checks) Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
Console.WriteLine($"\n{(checks.All(c => c.ok) ? "PASSED" : "FAILED")} ({checks.Count(c => c.ok)}/{checks.Length})");
return checks.All(c => c.ok) ? 0 : 1;

record Issue(int Id, string Title, string State, string[] Labels, string? Assignee);
