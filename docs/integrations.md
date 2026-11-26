# Connect to a model

Give the model one tool, using the name, description and input schema that Topwater provides. When the model calls
it, run the command and return the text.

| | Tool definition | Run a call |
|---|---|---|
| **.NET** | `BashTool.Name`, `BashTool.Description`, `BashTool.InputSchemaJson` | `await BashTool.ExecuteAsync(shell, command)` |
| **Python** | `topwater.TOOL_NAME`, `TOOL_DESCRIPTION`, `TOOL_INPUT_SCHEMA` | `await topwater.execute_tool(shell, command)` |
| **TypeScript** | `BashTool.name`, `BashTool.description`, `BashTool.inputSchema` | `await BashTool.execute(shell, command)` |

The tool is named `bash` and takes one string argument, `command`. The text that comes back is stdout, then stderr,
then `[exit code N]` when the command failed, and a note when a [limit](limits.md) cut something off. Use one shell
per conversation; a shell is not safe to share between threads.

If your framework is listed below, an adapter does this wiring for you.

| | Frameworks |
|---|---|
| .NET | [Microsoft.Extensions.AI](#microsoftextensionsai), [Semantic Kernel](#semantic-kernel), [Microsoft Agent Framework](#microsoft-agent-framework), the [OpenAI and Anthropic SDKs](#openai-and-anthropic-sdks) |
| Python | LangChain and LangGraph, LlamaIndex, OpenAI Agents SDK, Claude Agent SDK, Pydantic AI, CrewAI, smolagents, Google ADK: [python/README.md](../python/README.md#agent-frameworks) |
| TypeScript | Vercel AI SDK, OpenAI Agents, LangChain.js and LangGraph.js, Mastra, Claude Agent SDK: [typescript/README.md](../typescript/README.md#agent-frameworks) |
| Any MCP client | [`topwater mcp`](#mcp-clients), no code |

## The tool description

The default description tells the model the truth: it is in a sandboxed shell, git works on local repositories,
Python, the network and package installation are not available, and your programs are live. In testing this beat
both pretending to be an ordinary machine and announcing "this is a simulation". Every adapter lets you pass your
own description; the wording matters, so measure before you change it.

## Claude Agent SDK

The Claude Agent SDK is Claude Code as a library, and Claude Code has its own `Bash` tool that runs on the host.
Adding a Topwater tool next to it would leave that door open. `bash_options(shell)` in Python and
`bashOptions(shell)` in TypeScript return options for `query` that close it:

- the only tool is the shell, served by an in-process MCP server, so Claude sees it as `mcp__topwater__bash`;
- Claude Code's built-in tools are off (`tools=[]`), its real `Bash` among them;
- the developer's own Claude Code settings, allow rules and MCP servers stay out of the session
  (`setting_sources=[]` and `strict_mcp_config=True`; `settingSources: []` and `strictMcpConfig: true`).

Examples are in [python/README.md](../python/README.md#agent-frameworks) and
[typescript/README.md](../typescript/README.md#agent-frameworks).

## Microsoft.Extensions.AI

`Topwater.AI` turns a shell into an `AIFunction`, so any `IChatClient` (OpenAI, Azure OpenAI, Ollama, ...) can use it
through the standard function-invocation middleware. It depends only on `Microsoft.Extensions.AI.Abstractions`;
`UseFunctionInvocation` comes from the `Microsoft.Extensions.AI` package.

```csharp
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

IChatClient client = new ChatClientBuilder(innerClient).UseFunctionInvocation().Build();
var options = new ChatOptions { Tools = [shell.AsAIFunction()] };
List<ChatMessage> history = [new(ChatRole.User, "How many lines are in notes.txt?")];

var response = await client.GetResponseAsync(history, options);
history.AddMessages(response);
Console.WriteLine(response.Text);
```

The function is named `bash`, takes one string parameter `command` (the schema in `BashTool.InputSchemaJson`) and
returns the same text as `BashTool.ExecuteAsync`, `[exit code N]` included. `shell.AsAIFunction("...")` replaces the
description. Models may ask for several calls at once: calls
through functions made from the same shell run one at a time, in arrival order. Calling `shell.RunAsync` yourself
at the same time is not covered.

### Semantic Kernel

Semantic Kernel (1.4x and later) wraps any `AIFunction` as a kernel function, so the same function is a plugin with no
extra package:

```csharp
using Microsoft.SemanticKernel;
using Topwater.AI;

kernel.Plugins.AddFromFunctions("shell", [shell.AsAIFunction().AsKernelFunction()]);
var settings = new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
var answer = await kernel.InvokePromptAsync("How many lines are in notes.txt?", new(settings));
```

The model sees it as `shell-bash` (Semantic Kernel prefixes the plugin name), with the same description and schema.

Tested with Semantic Kernel 1.82. For new agents Microsoft points to Agent Framework, below.

### Microsoft Agent Framework

Agent Framework (`Microsoft.Agents.AI`, the successor to Semantic Kernel agents and AutoGen) builds agents on
`IChatClient` and takes `AITool`s, so the shell's function goes straight in:

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/notes.txt", "one\ntwo\n");

AIAgent agent = chatClient.AsAIAgent(instructions: "You are a helpful assistant.", tools: [shell.AsAIFunction()]);
AgentSession session = await agent.CreateSessionAsync();
AgentResponse response = await agent.RunAsync("How many lines are in notes.txt?", session);
Console.WriteLine(response.Text);
```

The agent invokes the tool itself; there is no `UseFunctionInvocation` to add. Tested with `Microsoft.Agents.AI` 1.28.
`examples/AgentFrameworkAgent` is a complete agent on OpenAI.

### OpenAI and Anthropic SDKs

Both official SDKs give an `IChatClient`, which then takes the function as above:

```csharp
IChatClient openai = new OpenAIClient(key).GetChatClient(model).AsIChatClient();   // package Microsoft.Extensions.AI.OpenAI
IChatClient claude = new AnthropicClient().AsIChatClient(model);                   // package Anthropic
```

If you drive the OpenAI SDK without `IChatClient`, `shell.AsAIFunction().AsOpenAIChatTool()` (or `AsOpenAIResponseTool()`)
gives its tool type; send the tool call's `command` to `BashTool.ExecuteAsync`. Tested with `OpenAI` 2.15 and `Anthropic`
13.14, against a fake HTTP server.

### MCP clients

Clients that speak MCP (Claude Desktop, Claude Code, Cursor, VS Code, or your own `ModelContextProtocol` client) need no
code: run [`topwater mcp`](cli.md), which serves the same `bash` tool over stdio. `Topwater.Mcp`
is the other direction: it mounts MCP servers inside the shell.
