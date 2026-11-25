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

The function is named `bash` and takes one `command` string. Calls from the same shell run one at a time.
See the [reference](https://github.com/gr-ant/Topwater/blob/main/docs/reference.md#microsoftextensionsai) for details.
