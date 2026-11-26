// The same agent loop as GeminiAgent, on OpenRouter's OpenAI-style chat API, so the examples can run on Claude, GPT and others.
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Topwater;

public static class OpenRouterAgent
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };
    public static string Model => Environment.GetEnvironmentVariable("MODEL") ?? "anthropic/claude-sonnet-5.5";

    public static async Task<AgentRun> RunAsync(Shell shell, string prompt, string key, string? toolDescription = null, string system = GeminiAgent.DefaultSystem, int maxTurns = 30, bool verbose = false)
    {
        var tools = JsonNode.Parse($$$"""[{"type":"function","function":{"name":"{{{BashTool.Name}}}","description":{{{JsonSerializer.Serialize(toolDescription ?? BashTool.Description)}}},"parameters":{{{BashTool.InputSchemaJson}}}}}]""")!;
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = prompt },
        };
        var commands = new List<AgentCommand>();
        for (int turn = 0; turn < maxTurns; turn++)
        {
            var request = new JsonObject { ["model"] = Model, ["messages"] = messages.DeepClone(), ["tools"] = tools.DeepClone() };
            var message = (await Call(request, key))?["choices"]?[0]?["message"]?.DeepClone() ?? new JsonObject { ["role"] = "assistant", ["content"] = "" };
            messages.Add(message);   // echoed back verbatim, reasoning fields included
            var calls = message["tool_calls"]?.AsArray() ?? [];
            if (calls.Count == 0) return new AgentRun((message["content"]?.GetValue<string>() ?? "").Trim(), commands);

            foreach (var call in calls)
            {
                string command;
                try { command = JsonNode.Parse(call!["function"]!["arguments"]!.GetValue<string>())?["command"]?.GetValue<string>() ?? ""; }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { command = ""; }   // malformed arguments (bad JSON, a duplicated key): run nothing, the model sees an empty result
                var result = await shell.RunAsync(command);
                commands.Add(new AgentCommand(command, result.ExitCode, result.Stdout, result.Stderr));
                if (verbose)
                    Console.WriteLine($"  $ {command.Trim().Split('\n')[0]}\n    -> exit {result.ExitCode}{(result.Stderr.Trim().Length > 0 ? " | " + result.Stderr.Trim().Split('\n')[0] : "")}");
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call!["id"]!.GetValue<string>(), ["content"] = result.ToToolOutput() });
            }
        }
        return new AgentRun("(turn limit reached)", commands);
    }

    static async Task<JsonNode?> Call(JsonObject request, string key)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/chat/completions") { Content = JsonContent.Create(request) };
            message.Headers.Add("Authorization", $"Bearer {key}");
            using var response = await Http.SendAsync(message);
            var body = await response.Content.ReadAsStringAsync();
            // OpenRouter can return 200 with an error object when the upstream provider fails
            if (response.IsSuccessStatusCode && JsonNode.Parse(body) is { } json && json["error"] is null) return json;
            if ((int)response.StatusCode is not (200 or 429 or 500 or 502 or 503) || attempt == 6)
                throw new InvalidOperationException($"model call failed: {(int)response.StatusCode} {body[..Math.Min(300, body.Length)]}");
            await Task.Delay(TimeSpan.FromSeconds(5 * Math.Pow(2, attempt)));
        }
    }
}
