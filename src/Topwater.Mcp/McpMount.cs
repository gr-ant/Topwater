using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Topwater.Mcp;

/// <summary>Options for <see cref="McpExtensions.MountMcpAsync(Shell, string, McpClient, McpMountOptions?, CancellationToken)"/>.</summary>
public sealed class McpMountOptions
{
    /// <summary>Shown in `help` and at the top of `program --help`. Defaults to the server's own description.</summary>
    public string? Description { get; init; }
    /// <summary>Return false to hide a tool from the agent (by its MCP name), e.g. to expose only read-only tools.</summary>
    public Func<string, bool>? Include { get; init; }
}

/// <summary>An MCP server mounted as a command. Dispose it to close a connection that the mount opened itself.</summary>
public sealed class McpMount : IAsyncDisposable
{
    readonly bool _ownsClient;

    internal McpMount(McpClient client, IReadOnlyList<string> tools, bool ownsClient)
    {
        Client = client; Tools = tools; _ownsClient = ownsClient;
    }

    public McpClient Client { get; }
    /// <summary>MCP names of the tools exposed to the agent.</summary>
    public IReadOnlyList<string> Tools { get; }

    public ValueTask DisposeAsync() => _ownsClient ? Client.DisposeAsync() : ValueTask.CompletedTask;
}

public static class McpExtensions
{
    /// <summary>
    /// Mounts a connected MCP server as one program: <c>program</c> lists its tools, <c>program TOOL --help</c>
    /// documents one, and <c>program TOOL --arg value</c> calls it. The model sees no tool definitions up front;
    /// it discovers them the way it would discover any command-line program.
    /// The tool list is read once, when mounting. The calls are real: they reach the server.
    /// </summary>
    public static async Task<McpMount> MountMcpAsync(this Shell shell, string program, McpClient client, McpMountOptions? options = null, CancellationToken cancellationToken = default)
        => await Mount(shell, program, client, options, ownsClient: false, cancellationToken);

    /// <summary>Starts a local MCP server (stdio) and mounts it, e.g. <c>MountMcpAsync("fs", "npx", ["-y", "@modelcontextprotocol/server-filesystem", "/data"])</c>.</summary>
    public static async Task<McpMount> MountMcpAsync(this Shell shell, string program, string command, IEnumerable<string>? arguments = null, McpMountOptions? options = null, CancellationToken cancellationToken = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions { Name = program, Command = command, Arguments = arguments?.ToList() });
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        try { return await Mount(shell, program, client, options, ownsClient: true, cancellationToken); }
        catch { await client.DisposeAsync(); throw; }
    }

    static async Task<McpMount> Mount(Shell shell, string program, McpClient client, McpMountOptions? options, bool ownsClient, CancellationToken ct)
    {
        var tools = (await client.ListToolsAsync(cancellationToken: ct))
            .Where(t => options?.Include?.Invoke(t.Name) ?? true)
            .Select(t => new ToolSpec(t.Name, t.Description, t.JsonSchema)).ToList();
        var description = options?.Description ?? client.ServerInfo?.Description ?? client.ServerInfo?.Title ?? $"{client.ServerInfo?.Name ?? program} tools";

        shell.RegisterTools(program, tools, async (tool, args, token) =>
        {
            var arguments = args.ToDictionary(kv => kv.Key, kv => (object?)JsonDocument.Parse(kv.Value?.ToJsonString() ?? "null").RootElement.Clone());
            CallToolResult result;
            try { result = await client.CallToolAsync(tool, arguments, cancellationToken: token); }
            catch (ModelContextProtocol.McpException e) { return new ToolResult(e.Message, IsError: true); }
            return new ToolResult(Render(result), result.IsError == true);
        }, description);
        return new McpMount(client, tools.Select(t => t.Name).ToList(), ownsClient);
    }

    static string Render(CallToolResult result)
    {
        var parts = new List<string>();
        foreach (var block in result.Content ?? [])
            parts.Add(block switch
            {
                TextContentBlock t => t.Text,
                EmbeddedResourceBlock { Resource: TextResourceContents r } => r.Text,
                ImageContentBlock i => $"[image, {i.MimeType}]",
                _ => $"[{block.Type} content]",
            });
        if (parts.Count == 0 && result.StructuredContent is { } structured) parts.Add(structured.ToString() ?? "");
        return string.Join("\n", parts);
    }
}
