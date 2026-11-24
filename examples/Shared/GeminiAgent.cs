// A minimal agent loop on the Gemini API, shared by the examples: the model gets one tool (bash) and a Topwater shell.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Topwater;

public sealed record AgentCommand(string Command, int ExitCode, string Stdout, string Stderr);
public sealed record AgentRun(string Answer, List<AgentCommand> Commands);

public static class GeminiAgent
{
    public const string DefaultSystem = "You are an autonomous agent working through a bash tool. Use the tool to inspect and act; " +
        "do not ask the user questions. When finished, reply with a short final answer.";

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    static readonly string Url = $"https://generativelanguage.googleapis.com/v1beta/models/{Environment.GetEnvironmentVariable("MODEL") ?? "gemini-flash-latest"}:generateContent";

    public static async Task<AgentRun> RunAsync(Shell shell, string prompt, string key, string? toolDescription = null, string system = DefaultSystem, int maxTurns = 30, bool verbose = false)
    {
        var tools = JsonNode.Parse($$"""[{"functionDeclarations":[{"name":"{{BashTool.Name}}","description":{{JsonSerializer.Serialize(toolDescription ?? BashTool.Description)}},"parameters":{{BashTool.InputSchemaJson}}}]}]""")!;
        var contents = new JsonArray { new JsonObject { ["role"] = "user", ["parts"] = new JsonArray { new JsonObject { ["text"] = prompt } } } };
        var commands = new List<AgentCommand>();
        for (int turn = 0; turn < maxTurns; turn++)
        {
            var request = new JsonObject
            {
                ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = system } } },
                ["contents"] = contents.DeepClone(), ["tools"] = tools.DeepClone(),
            };
            var content = (await Call(request, key))?["candidates"]?[0]?["content"]?.DeepClone() ?? JsonNode.Parse("""{"role":"model","parts":[]}""")!;
            contents.Add(content);   // echoed back verbatim, which keeps the model's thought signatures
            var parts = content["parts"]?.AsArray() ?? [];
            var calls = parts.Where(p => p?["functionCall"] is not null).ToList();
            if (calls.Count == 0)
                return new AgentRun(string.Concat(parts.Where(p => p?["thought"] is null).Select(p => p?["text"]?.GetValue<string>())).Trim(), commands);

            var replies = new JsonArray();
            foreach (var call in calls)
            {
                string command = call!["functionCall"]!["args"]?["command"]?.GetValue<string>() ?? "";
                var result = await shell.RunAsync(command);
                commands.Add(new AgentCommand(command, result.ExitCode, result.Stdout, result.Stderr));
                if (verbose)
                    Console.WriteLine($"  $ {command.Trim().Split('\n')[0]}\n    -> exit {result.ExitCode}{(result.Stderr.Trim().Length > 0 ? " | " + result.Stderr.Trim().Split('\n')[0] : "")}");
                replies.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = call["functionCall"]!["name"]!.GetValue<string>(), ["response"] = new JsonObject { ["output"] = result.ToToolOutput() } } });
            }
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = replies });
        }
        return new AgentRun("(turn limit reached)", commands);
    }

    static async Task<JsonNode?> Call(JsonObject request, string key)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(request) };
            message.Headers.Add("x-goog-api-key", key);
            using var response = await Http.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) return JsonNode.Parse(body);
            // rate limits and server errors back off and retry
            if ((int)response.StatusCode is not (429 or 500 or 503) || attempt == 6)
                throw new InvalidOperationException($"model call failed: {(int)response.StatusCode} {body[..Math.Min(300, body.Length)]}");
            await Task.Delay(TimeSpan.FromSeconds(5 * Math.Pow(2, attempt)));
        }
    }
}
