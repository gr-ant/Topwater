using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Topwater.AI;

namespace Topwater.Cli;

/// <summary>The machine as an MCP server with one tool, <c>bash</c>: the same name, schema and output as <see cref="BashTool"/>.</summary>
static class McpServerCommand
{
    /// <summary>
    /// The server on newline-delimited JSON-RPC streams (stdin and stdout for <c>topwater mcp</c>). At end of input it
    /// answers every request it has read before it stops.
    /// </summary>
    public static McpServer Create(Machine machine, Stream input, Stream output)
    {
        var count = new Counts();
        // shortcut: one call limit plus 10 s for everything still to answer at end of input; calls queued behind a long one may be cut
        var bound = machine.Shell.Options.MaxCallDuration + TimeSpan.FromSeconds(10);
        var transport = new CountingTransport(new StreamServerTransport(new DrainingInput(input, count, bound), output, "topwater"), count);
        return McpServer.Create(transport, new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "topwater", Version = Version },
            ToolCollection = [McpServerTool.Create(new SavingFunction(machine))],
        });
    }

    // The SDK ends the session at end of input, cancelling the requests still running and refusing to write their
    // answers, so a client that pipes requests in and closes its end would get nothing. The input below holds back the
    // end until as many answers have been written as requests were read.
    sealed class Counts { public int Requests, Answers; }

    /// <summary>Counts the requests (lines with a method and an id) passing through, and reports the end only once each is answered.</summary>
    sealed class DrainingInput(Stream inner, Counts count, TimeSpan bound) : Stream
    {
        readonly MemoryStream _line = new();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken);
            if (n > 0) { Scan(buffer.Span[..n]); return n; }
            Scan("\n"u8);   // a last line without a newline
            var waited = Stopwatch.StartNew();
            while (Volatile.Read(ref count.Answers) < Volatile.Read(ref count.Requests) && waited.Elapsed < bound) await Task.Delay(10, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        void Scan(ReadOnlySpan<byte> data)
        {
            while (data.IndexOf((byte)'\n') is var end and >= 0)
            {
                _line.Write(data[..end]);
                if (IsRequest(_line.GetBuffer().AsSpan(0, (int)_line.Length))) Interlocked.Increment(ref count.Requests);
                _line.SetLength(0);
                data = data[(end + 1)..];
            }
            _line.Write(data);
        }

        static bool IsRequest(ReadOnlySpan<byte> line)
        {
            try
            {
                var reader = new Utf8JsonReader(line);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                bool id = false, method = false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("id"u8)) { reader.Read(); id = reader.TokenType != JsonTokenType.Null; }
                    else if (reader.ValueTextEquals("method"u8)) { method = true; reader.Skip(); }
                    else reader.Skip();
                }
                return id && method;
            }
            catch (JsonException) { return false; }   // the SDK answers nothing to a line it cannot parse either
        }

        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts the answers (responses and errors) once they are written.</summary>
    sealed class CountingTransport(ITransport inner, Counts count) : ITransport
    {
        public string? SessionId => inner.SessionId;
        public System.Threading.Channels.ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public ValueTask DisposeAsync() => inner.DisposeAsync();

        public async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
        {
            await inner.SendMessageAsync(message, cancellationToken);
            if (message is JsonRpcResponse or JsonRpcError) Interlocked.Increment(ref count.Answers);
        }
    }

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
