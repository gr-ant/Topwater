using System.Text.Json;
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

namespace Topwater.Tests;

/// <summary>The shell as a Microsoft.Extensions.AI function.</summary>
public class AIFunctionTests
{
    static Task<object?> Call(AIFunction f, object command) => f.InvokeAsync(new AIFunctionArguments { ["command"] = command }).AsTask();

    [Fact]
    public void DescribesItselfAsTheBashTool()
    {
        var f = new Shell().AsAIFunction();
        Assert.Equal("bash", f.Name);
        Assert.Equal(BashTool.Description, f.Description);
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(BashTool.InputSchemaJson).RootElement, f.JsonSchema));
        Assert.Equal("Use the shell.", new Shell().AsAIFunction("Use the shell.").Description);
    }

    [Fact]
    public async Task RunsCommandsAndKeepsState()
    {
        var f = new Shell().AsAIFunction();
        Assert.Equal("(no output)", await Call(f, "cd /tmp && x=5 && echo hi > a.txt"));
        Assert.Equal("/tmp\nhi\n", await Call(f, JsonDocument.Parse("\"pwd; cat a.txt\"").RootElement));   // how providers pass arguments
        Assert.Equal("cat: nope: No such file or directory\n[exit code 1]", await Call(f, "cat nope"));
        Assert.Contains("[exit code 2]", (string)(await f.InvokeAsync(new AIFunctionArguments()))!);
    }

    [Fact]
    public async Task HonoursCancellation()
    {
        var f = new Shell().AsAIFunction();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.InvokeAsync(new AIFunctionArguments { ["command"] = "sleep 30" }, cts.Token).AsTask());
    }

    [Fact]
    public async Task ParallelCallsAreSerialised()
    {
        var shell = new Shell();
        // two functions over one shell share its lock
        var (a, b) = (shell.AsAIFunction(), shell.AsAIFunction());
        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => Call(i % 2 == 0 ? a : b,
            $"n=$(cat /tmp/n 2>/dev/null || echo 0); for j in 1 2 3 4 5 6 7 8 9 10; do :; done; echo $((n + 1)) > /tmp/n; cd /home/agent; echo {i} >> log"))));
        Assert.Equal("40", shell.Fs.ReadAllText("/tmp/n").Trim());
        Assert.Equal(40, shell.Fs.ReadAllText("/home/agent/log").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    /// <summary>A model that asks for one bash call, then answers with what the tool returned.</summary>
    sealed class FakeModel : IChatClient
    {
        public List<string> ToolsOffered { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ToolsOffered.AddRange(options?.Tools?.Select(t => t.Name) ?? []);
            var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
            return Task.FromResult(result is null
                ? new ChatResponse(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "bash", new Dictionary<string, object?> { ["command"] = "wc -l < notes.txt; exit 3" })]))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, $"The tool said: {result.Result}")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    [Fact]
    public async Task WorksThroughTheFunctionInvokingChatClient()
    {
        var model = new FakeModel();
        IChatClient innerClient = model;

        // the docs/integrations.md example, verbatim from here down
        var shell = new Shell();
        shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

        IChatClient client = new ChatClientBuilder(innerClient).UseFunctionInvocation().Build();
        var options = new ChatOptions { Tools = [shell.AsAIFunction()] };
        List<ChatMessage> history = [new(ChatRole.User, "How many lines are in notes.txt?")];

        var response = await client.GetResponseAsync(history, options);
        history.AddMessages(response);
        Console.WriteLine(response.Text);
        // end of the example

        Assert.Equal("The tool said: 2\n[exit code 3]", response.Text);
        Assert.Equal(["bash", "bash"], model.ToolsOffered);
    }
}
