# Topwater.AI

Use a [Topwater](https://github.com/gr-ant/Topwater) shell as the one `bash` tool of any Microsoft.Extensions.AI `IChatClient`
(OpenAI, Azure OpenAI, Ollama, ...).

```bash
dotnet add package Topwater.AI --prerelease
```

```csharp
using Microsoft.Extensions.AI;
using Topwater;
using Topwater.AI;

var shell = new Shell();
IChatClient client = new ChatClientBuilder(innerClient).UseFunctionInvocation().Build();
var options = new ChatOptions { Tools = [shell.AsAIFunction()] };

var response = await client.GetResponseAsync("How many lines are in notes.txt?", options);
```

Semantic Kernel takes the same function, with no extra package (`AsKernelFunction` is in Semantic Kernel itself):

```csharp
using Microsoft.SemanticKernel;

kernel.Plugins.AddFromFunctions("shell", [shell.AsAIFunction().AsKernelFunction()]);
var settings = new PromptExecutionSettings { FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() };
```

Microsoft Agent Framework (`Microsoft.Agents.AI`) takes it as a tool, with no extra package:

```csharp
using Microsoft.Agents.AI;

AIAgent agent = chatClient.AsAIAgent(instructions: "You are a helpful assistant.", tools: [shell.AsAIFunction()]);
Console.WriteLine(await agent.RunAsync("How many lines are in notes.txt?"));
```

The OpenAI and Anthropic .NET SDKs give an `IChatClient` (`chatClient.AsIChatClient()` from Microsoft.Extensions.AI.OpenAI,
`anthropicClient.AsIChatClient(model)` from Anthropic). For MCP clients such as Claude Desktop or VS Code, run
`topwater mcp` (package Topwater.Cli) instead.

The function is named `bash` and takes one `command` string. Calls from the same shell run one at a time.
See the [reference](https://github.com/gr-ant/Topwater/blob/main/docs/reference.md#microsoftextensionsai) for details.
