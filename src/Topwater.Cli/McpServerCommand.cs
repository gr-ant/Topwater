using System.Reflection;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topwater.AI;

namespace Topwater.Cli;

/// <summary>The machine as an MCP server with one tool, <c>bash</c>: the same name, schema and output as <see cref="BashTool"/>.</summary>
static class McpServerCommand
{
    public static McpServer Create(Machine machine, ITransport transport) => McpServer.Create(transport, new McpServerOptions
    {
        ServerInfo = new Implementation { Name = "topwater", Version = Version },
        ToolCollection = [McpServerTool.Create(new SavingFunction(machine))],
    });

    /// <summary><see cref="BashTool.Description"/> without its last sentence about the app's own programs: this server has none.</summary>
    public static string Description { get; } =
        BashTool.Description[..BashTool.Description.IndexOf("Other programs", StringComparison.Ordinal)] + "Run `help` to list the commands and `<command> --help` for usage.";

    public static string Version { get; } =
        typeof(McpServerCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The shell's AIFunction, with the state file written after each call while no other call can change the machine.</summary>
    sealed class SavingFunction(Machine machine) : DelegatingAIFunction(machine.Shell.AsAIFunction(McpServerCommand.Description))
    {
        readonly SemaphoreSlim _gate = new(1, 1);

        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var result = await base.InvokeCoreAsync(arguments, cancellationToken);
                machine.Save();
                return result;
            }
            finally { _gate.Release(); }
        }
    }
}
