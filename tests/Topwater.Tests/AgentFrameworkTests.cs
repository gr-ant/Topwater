using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using Anthropic;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Topwater;
using Topwater.AI;

namespace Topwater.Tests;

/// <summary>
/// The shell's AIFunction in other .NET agent stacks, with no glue code: Microsoft Agent Framework, and the OpenAI and
/// Anthropic SDKs' own IChatClient adapters (driven through a fake HTTP server, so their tool-schema translation is real).
/// </summary>
public class AgentFrameworkTests
{
    /// <summary>A model that asks for one bash call, then answers with what the tool returned.</summary>
    sealed class FakeModel : IChatClient
    {
        public List<string> ToolsOffered { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ToolsOffered.AddRange(options?.Tools?.Select(t => t.Name) ?? []);
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            return Task.FromResult(result is null
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "bash", new Dictionary<string, object?> { ["command"] = "wc -l < notes.txt" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, $"The tool said: {result.Result}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task MicrosoftAgentFramework()
    {
        var model = new FakeModel();
        IChatClient chatClient = model;

        // the docs/integrations.md example, verbatim from here down
        var shell = new Shell();
        shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

        AIAgent agent = chatClient.AsAIAgent(instructions: "You are a helpful assistant.", tools: [shell.AsAIFunction()]);
        AgentSession session = await agent.CreateSessionAsync();
        AgentResponse response = await agent.RunAsync("How many lines are in notes.txt?", session);
        Console.WriteLine(response.Text);
        // end of the example

        Assert.Equal("The tool said: 2\n", response.Text);
        Assert.Contains("bash", model.ToolsOffered);
    }

    /// <summary>Answers each request with the next canned JSON body and keeps the request bodies.</summary>
    sealed class FakeServer(params string[] responses) : HttpMessageHandler
    {
        readonly Queue<string> queue = new(responses);
        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(queue.Dequeue(), System.Text.Encoding.UTF8, "application/json") };
        }
    }

    static async Task<string> Ask(IChatClient inner, Shell shell)
    {
        IChatClient client = new ChatClientBuilder(inner).UseFunctionInvocation().Build();
        return (await client.GetResponseAsync("How many lines are in notes.txt?", new ChatOptions { Tools = [shell.AsAIFunction()] })).Text;
    }

    static Shell NotesShell()
    {
        var shell = new Shell();
        shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");
        return shell;
    }

    [Fact]
    public async Task OpenAIChatCompletions()
    {
        var server = new FakeServer(
            """{"id":"c1","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"bash","arguments":"{\"command\":\"wc -l < notes.txt\"}"}}]}}]}""",
            """{"id":"c2","object":"chat.completion","created":0,"model":"m","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Two lines."}}]}""");
        var openai = new OpenAIClient(new ApiKeyCredential("test"), new OpenAIClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(server)) });

        Assert.Equal("Two lines.", await Ask(openai.GetChatClient("m").AsIChatClient(), NotesShell()));

        var tool = server.Requests[0].GetProperty("tools")[0].GetProperty("function");
        Assert.Equal("bash", tool.GetProperty("name").GetString());
        Assert.Equal(BashTool.Description, tool.GetProperty("description").GetString());
        Assert.Equal(["command"], tool.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var result = server.Requests[1].GetProperty("messages").EnumerateArray().Last();
        Assert.Equal("tool", result.GetProperty("role").GetString());
        Assert.Equal("2\n", result.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AnthropicMessages()
    {
        var server = new FakeServer(
            """{"id":"m1","type":"message","role":"assistant","model":"m","stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1},"content":[{"type":"tool_use","id":"toolu_1","name":"bash","input":{"command":"wc -l < notes.txt"}}]}""",
            """{"id":"m2","type":"message","role":"assistant","model":"m","stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1},"content":[{"type":"text","text":"Two lines."}]}""");
        var anthropic = new AnthropicClient { ApiKey = "test", HttpClient = new HttpClient(server) };

        Assert.Equal("Two lines.", await Ask(anthropic.AsIChatClient("m"), NotesShell()));

        var tool = server.Requests[0].GetProperty("tools")[0];
        Assert.Equal("bash", tool.GetProperty("name").GetString());
        Assert.Equal(BashTool.Description, tool.GetProperty("description").GetString());
        Assert.Equal(["command"], tool.GetProperty("input_schema").GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        var result = server.Requests[1].GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Contains("2\n", result.GetRawText().Replace("\\n", "\n"));
    }
}
