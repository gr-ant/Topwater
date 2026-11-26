// A Microsoft Agent Framework agent whose one tool is a Topwater shell.
//
//   OPENAI_API_KEY=... dotnet run --project examples/AgentFrameworkAgent
//   OPENAI_API_KEY=... OPENAI_MODEL=gpt-5-mini dotnet run --project examples/AgentFrameworkAgent "your question"
//
// Any IChatClient works the same way: Azure OpenAI, Ollama, or Anthropic's (anthropicClient.AsIChatClient(model)).
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using Topwater;
using Topwater.AI;

var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrEmpty(key)) { Console.Error.WriteLine("set OPENAI_API_KEY"); return 2; }

var shell = new Shell();
shell.Fs.WriteAllText("/home/agent/sales.csv", "region,amount\nnorth,120\nsouth,80\nnorth,45\neast,200\nsouth,15\n");

IChatClient chatClient = new OpenAIClient(key).GetChatClient(Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-5-mini").AsIChatClient();
AIAgent agent = chatClient.AsAIAgent(instructions: "Answer using the bash tool. Files are in the home directory.", tools: [shell.AsAIFunction()]);

var question = args.Length > 0 ? string.Join(' ', args) : "What is the total amount per region in sales.csv, largest first?";
Console.WriteLine(await agent.RunAsync(question));
return 0;
