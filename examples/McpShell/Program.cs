// A terminal on the simulated machine, optionally with one MCP server mounted as a command.
//
//   dotnet run --project examples/McpShell
//   dotnet run --project examples/McpShell -- NAME COMMAND [ARGS...]     (COMMAND starts a stdio MCP server)
//
// Then type what an agent would: `help`, `NAME --help`, `NAME some-tool --arg value | jq .`.
using Topwater;
using Topwater.Mcp;

if (args.Length == 1)
{
    Console.Error.WriteLine("usage: McpShell [NAME COMMAND [ARGS...]]");
    return 2;
}

var shell = new Shell();
McpMount? mount = null;
if (args.Length >= 2)
{
    mount = await shell.MountMcpAsync(args[0], args[1], args.Skip(2));
    Console.Error.WriteLine($"Mounted {mount.Tools.Count} tools as `{args[0]}`. Try `{args[0]} --help`.");
}

while (true)
{
    if (!Console.IsInputRedirected) Console.Write("agent@topwater:~$ ");
    if (Console.ReadLine() is not { } line) break;
    if (line.Trim().Length > 0) Console.Write(await BashTool.ExecuteAsync(shell, line));
}
if (mount is not null) await mount.DisposeAsync();
return 0;
