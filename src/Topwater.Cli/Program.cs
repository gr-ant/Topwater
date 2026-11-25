using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;

namespace Topwater.Cli;

/// <summary>Where a subcommand reads and writes; <see cref="Terminal"/> is false when stdin is a pipe or file.</summary>
sealed record Io(TextReader In, TextWriter Out, TextWriter Err, bool Terminal);

/// <summary>A subcommand. <see cref="Run"/> is null for one that exists in other builds only.</summary>
sealed record Command(string Usage, string Summary, Func<string[], Io, CancellationToken, Task<int>>? Run, string? Unavailable = null);

static class Program
{
    static Task<int> Main(string[] args) =>
        Cli.RunAsync(args, new Io(Console.In, Console.Out, Console.Error, !Console.IsInputRedirected && !Console.IsOutputRedirected), CancellationToken.None);
}

static class Cli
{
    /// <summary>
    /// Every subcommand. A build with more features adds its entries here; the ones with a null handler are listed in
    /// <c>topwater --help</c> and answer with their "not available" message until then.
    /// </summary>
    internal static readonly Dictionary<string, Command> Commands = new(StringComparer.Ordinal)
    {
        ["shell"] = new("shell [options]", "an interactive shell on the simulated machine (the default)", (a, io, ct) => ShellCommand(a, io, ct)),
        ["run"] = new("run [options] \"<command>\"", "run one command, print its output, exit with its exit code", RunCommand),
        ["mcp"] = new("mcp [options]", "serve the machine over stdio as an MCP server with one `bash` tool", McpCommand),
        ["snapshot"] = new("snapshot save|load|info FILE [options]", "write the machine to FILE, open a shell on FILE, or describe FILE", SnapshotCommand),
        ["export"] = new("export FILE [options]", "write the machine (and its history, if it has one) to FILE as a .topwater file", ExportCommand),
        ["import"] = new("import FILE --state STATE", "check FILE and make its machine the --state file", ImportCommand),
        ["replay"] = new("replay FILE [options]", "re-run a recorded session and report every call that prints something else", ReplayCommand),
    };

    const string OptionsHelp = """
        Options (every subcommand):
          --state FILE        load the machine from FILE if it exists; save it there after every command and on exit
          --from FILE         start from the snapshot FILE when there is no --state file yet (FILE is never written)
          --import HOST:PATH  copy the host file or directory HOST into the machine at PATH when it starts (a copy, never live)
          --export PATH:HOST  copy PATH out of the machine to the host path HOST when it exits
          --timeout SECONDS   time limit for one command (default 120)
          --disk MB           size of the simulated disk (default 128)

        Without --import or --export nothing on this computer is read or written, except the --state and --from files.
        """;

    public static string Usage()
    {
        var lines = Commands.Select(c => $"  topwater {c.Value.Usage,-42} {c.Value.Summary}{(c.Value.Run == null ? " (not available in this build)" : "")}");
        return $"Topwater {McpServerCommand.Version}: a simulated Linux shell for agents. Nothing typed into it reaches this computer.\n\n" +
               $"Usage:\n{string.Join('\n', lines)}\n\n{OptionsHelp}\n";
    }

    public static async Task<int> RunAsync(string[] args, Io io, CancellationToken ct)
    {
        try
        {
            if (args is ["-h" or "--help" or "help", ..]) { io.Out.Write(Usage()); return 0; }
            if (args is ["--version"]) { io.Out.WriteLine(McpServerCommand.Version); return 0; }
            var (command, rest) = args.Length > 0 && Commands.TryGetValue(args[0], out var c) ? (c, args[1..]) : (Commands["shell"], args);
            if (command.Run == null)
            {
                io.Err.WriteLine($"topwater {args[0]}: not available in this build ({command.Unavailable})");
                return 1;
            }
            return await command.Run(rest, io, ct);
        }
        catch (CliException e)
        {
            io.Err.WriteLine($"topwater: {e.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Reads the shared options. Options may come anywhere, except that with <paramref name="stopAtCommand"/> the first word that
    /// is not an option starts the command, so <c>topwater run ls -la</c> passes <c>-la</c> to ls.
    /// </summary>
    internal static (MachineOptions Options, List<string> Words, bool Help) Parse(string[] args, bool stopAtCommand = false)
    {
        var o = new MachineOptions();
        var rest = new List<string>();
        var help = false;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (rest.Count > 0 && stopAtCommand || a == "--") { rest.AddRange(args[(a == "--" ? i + 1 : i)..]); break; }
            string Value() => i + 1 < args.Length ? args[++i] : throw new CliException($"{a} needs a value");
            switch (a)
            {
                case "-h" or "--help": help = true; break;
                case "--state": o.State = Value(); break;
                case "--from": o.From = Value(); break;
                case "--import":
                {
                    var v = Value();
                    var at = v.LastIndexOf(':');   // the host side may be C:\...; the machine path has no colon
                    if (at <= 0 || at == v.Length - 1) throw new CliException($"--import {v}: expected HOST:PATH, e.g. ./proj:/home/agent/proj");
                    o.Imports.Add((v[..at], v[(at + 1)..]));
                    break;
                }
                case "--export":
                {
                    var v = Value();
                    var at = v.IndexOf(':');
                    if (at <= 0 || at == v.Length - 1) throw new CliException($"--export {v}: expected PATH:HOST, e.g. /home/agent/proj:./out");
                    o.Exports.Add((v[..at], v[(at + 1)..]));
                    break;
                }
                case "--timeout":
                    o.TimeoutSeconds = double.TryParse(Value(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : throw new CliException("--timeout takes a number of seconds");
                    break;
                case "--disk":
                    o.DiskMegabytes = long.TryParse(Value(), out var d) && d is > 0 and < 1L << 40 ? d : throw new CliException("--disk takes a number of megabytes");
                    break;
                default:
                    if (a.StartsWith('-') && a.Length > 1 && rest.Count == 0 || a.StartsWith("--")) throw new CliException($"unknown option {a} (see topwater --help)");
                    rest.Add(a);
                    break;
            }
        }
        return (o, rest, help);
    }

    static string Prompt(Shell shell)
    {
        var home = shell.Environment.GetValueOrDefault("HOME", "/home/" + shell.Options.User);
        var cwd = shell.Cwd;
        if (cwd == home || cwd.StartsWith(home + "/")) cwd = "~" + cwd[home.Length..];
        return $"{shell.Options.User}@{shell.Options.Hostname}:{cwd}$ ";
    }

    static readonly Regex ExitLine = new(@"^\s*exit(\s+(?<code>\d+))?\s*;?\s*$");

    static async Task<int> ShellCommand(string[] args, Io io, CancellationToken ct, MachineOptions? preset = null)
    {
        var (o, rest, help) = preset is null ? Parse(args) : (preset, [], false);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest.Count > 0) throw new CliException($"unknown command {rest[0]} (see topwater --help)");
        var machine = Machine.Open(o, io.Err);
        var shell = machine.Shell;

        // piped input is a script: run it whole, so multi-line constructs work, and pass its exit code through
        if (!io.Terminal)
        {
            var script = await io.In.ReadToEndAsync(ct);
            return await RunAndPrint(machine, script, io, ct);
        }

        io.Err.WriteLine($"Topwater {McpServerCommand.Version}: a simulated machine; nothing you type reaches this computer. `help` lists the programs, `exit` or Ctrl+D quits.");
        var editor = new LineEditor([.. shell.History]);
        var exitCode = 0;
        while (editor.ReadLine(Prompt(shell)) is { } line)
        {
            if (line.Trim().Length == 0) continue;
            // shortcut: only a bare `exit [n]` ends the session (inside a compound command it ends that command); detect ExitSignal if that matters
            if (ExitLine.Match(line) is { Success: true } exit) { exitCode = exit.Groups["code"].Success ? int.Parse(exit.Groups["code"].Value) & 255 : 0; break; }

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ConsoleCancelEventHandler onCtrlC = (_, e) => { e.Cancel = true; cancel.Cancel(); };
            Console.CancelKeyPress += onCtrlC;
            string output;
            // the same text the model gets and the web shell shows: stdout, stderr, [exit code N], a limit's trailer
            try { output = await BashTool.ExecuteAsync(shell, line, cancel.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { output = "^C\n"; }
            finally { Console.CancelKeyPress -= onCtrlC; }
            if (output != "(no output)") io.Out.Write(output.EndsWith('\n') ? output : output + "\n");
            machine.Save();
        }
        if (!machine.Close() && exitCode == 0) exitCode = 1; // an export that failed
        return exitCode;
    }

    static async Task<int> RunAndPrint(Machine machine, string command, Io io, CancellationToken ct)
    {
        var result = await machine.Shell.RunAsync(command, ct);
        io.Out.Write(result.Stdout);
        io.Err.Write(result.Stderr);
        if (result.Trailer != null) io.Err.WriteLine(result.Trailer);
        if (!machine.Close() && result.ExitCode == 0) return 1; // an export that failed
        return result.ExitCode;
    }

    static async Task<int> RunCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args, stopAtCommand: true);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest.Count == 0) throw new CliException("run: missing the command, e.g. topwater run \"ls -la\"");
        return await RunAndPrint(Machine.Open(o, io.Err), string.Join(' ', rest), io, ct);
    }

    static async Task<int> McpCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest.Count > 0) throw new CliException($"mcp: unexpected argument {rest[0]}");
        var machine = Machine.Open(o, io.Err);
        // stdout carries the protocol, so everything for people goes to stderr (MCP clients show it in their logs)
        io.Err.WriteLine($"topwater mcp {McpServerCommand.Version}: serving one simulated machine over stdio. " +
            (o.Imports.Count + o.Exports.Count == 0 ? "No host files are copied in or out" : $"{o.Imports.Count} import(s), {o.Exports.Count} export(s) on exit") +
            (o.State != null ? $"; the machine is saved to {o.State}." : "; the machine is discarded on exit."));

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // a client stopping the server sends SIGTERM (or closes stdin); either way the state is saved and exports run
        using var onTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        using var onInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; stop.Cancel(); });
        try
        {
            await using var server = McpServerCommand.Create(machine, new StdioServerTransport("topwater"));
            await server.RunAsync(stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { machine.Close(); }
        return 0;
    }

    static async Task<int> SnapshotCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest is not [var verb, var file]) throw new CliException("snapshot: expected save|load|info FILE");
        switch (verb)
        {
            case "save":
            {
                var machine = Machine.Open(o, io.Err);
                Machine.SaveTo(machine.Shell, file);
                io.Out.WriteLine($"saved {file}");
                return 0;
            }
            case "load":
                if (!File.Exists(file)) throw new CliException($"snapshot load: {file}: no such file");
                o.From = file;
                o.State = null;   // a look at the file, never a change to it
                return await ShellCommand([], io, ct, o);
            case "info":
                io.Out.Write(Describe(file, o.ToShellOptions()));
                return 0;
            default:
                throw new CliException($"snapshot: unknown action {verb} (save, load or info)");
        }
    }

    /// <summary>Loads a .topwater file, a snapshot or an older JSON Lines recording as a recording, mapping load errors to <see cref="CliException"/>.</summary>
    static Recording LoadRecording(string file, ShellOptions limits)
    {
        try { return Recording.Load(file, limits); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CliException(e is FileNotFoundException ? $"{file}: no such file" : $"{file}: {e.Message}");
        }
    }

    /// <summary>The machine a file holds: a .topwater file's manifest, or for an older JSON Lines recording the machine after its last call.</summary>
    static async Task<ShellSnapshot> MachineOf(string file, ShellOptions limits, CancellationToken ct)
    {
        var recording = LoadRecording(file, limits);
        if (recording.Calls.Count == 0) return recording.Start;
        if (recording.Calls[^1].SnapshotAfter is { } end) return end;
        return (await recording.ShellAtAsync(recording.Calls.Count - 1, cancellationToken: ct)).Snapshot();
    }

    static void Write(string path, Action<Stream> save)
    {
        var temp = path + ".tmp";
        try
        {
            using (var file = File.Create(temp)) save(file);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new CliException($"{path}: {e.Message}"); }
    }

    static Task<int> ExportCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args);
        if (help) { io.Out.Write(Usage()); return Task.FromResult(0); }
        if (rest is not [var file]) throw new CliException("export: expected one FILE, e.g. topwater export --state machine.topwater copy.topwater");
        // a source with history keeps it, which also turns an older .jsonl recording into a .topwater file
        var source = o.State is { } s && File.Exists(s) ? s : o.From;
        if (source != null && LoadRecording(source, o.ToShellOptions()) is { Calls.Count: > 0 } recording)
        {
            if (o.Imports.Count > 0) throw new CliException($"export: {source} has history; --import would change the machine without it");
            Write(file, recording.Save);
            io.Out.WriteLine($"exported {file} ({recording.Calls.Count} calls)");
            return Task.FromResult(0);
        }
        var machine = Machine.Open(o, io.Err);
        Write(file, machine.Shell.Save);
        io.Out.WriteLine($"exported {file}");
        return Task.FromResult(0);
    }

    static async Task<int> ImportCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest is not [var file]) throw new CliException("import: expected one FILE, e.g. topwater import run.topwater --state machine.topwater");
        if (o.State is not { } state) throw new CliException("import: --state STATE is required: the file to write the machine to");
        if (o.From != null || o.Imports.Count + o.Exports.Count > 0) throw new CliException("import: only --state, --timeout and --disk apply");
        var snapshot = await MachineOf(file, o.ToShellOptions(), ct);
        Write(state, snapshot.Save);
        io.Out.WriteLine($"imported {file} into {state}");
        return 0;
    }

    static async Task<int> ReplayCommand(string[] args, Io io, CancellationToken ct)
    {
        var (o, rest, help) = Parse(args);
        if (help) { io.Out.Write(Usage()); return 0; }
        if (rest is not [var file]) throw new CliException("replay: expected one FILE, e.g. topwater replay run.topwater");
        if (o.State != null || o.From != null || o.Imports.Count + o.Exports.Count > 0) throw new CliException("replay: only --timeout and --disk apply");
        var recording = LoadRecording(file, o.ToShellOptions());
        // a deterministic run must match exactly; another differs in times and random names, which are normalised away
        var report = await recording.ReplayAsync(new ReplayOptions { Normalize = recording.IsDeterministic ? null : ReplayNormalizers.Volatile }, cancellationToken: ct);
        io.Out.Write(report.ToString());
        return report.Matches ? 0 : 1;
    }

    /// <summary>Checks the file the way Load does, then summarises its manifest.</summary>
    internal static string Describe(string file, ShellOptions limits)
    {
        try
        {
            using var stream = File.OpenRead(file);
            ShellSnapshot.Load(stream, limits);
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            using var manifest = zip.GetEntry("manifest.json")!.Open();
            var m = JsonDocument.Parse(manifest).RootElement;
            int Count(string name) => m.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
            var history = zip.GetEntry("history.jsonl") != null ? LoadRecording(file, limits) : null;
            var files = m.GetProperty("files").EnumerateArray().ToList();
            var regular = files.Where(f => f.GetProperty("type").GetString() == "file").ToList();
            long bytes = regular.Sum(f => f.TryGetProperty("size", out var s) ? s.GetInt64() : 0);
            return $"""
                {file}: topwater-snapshot version {m.GetProperty("version").GetInt32()}
                  created   {m.GetProperty("created").GetString()}
                  machine   {m.GetProperty("user").GetString()}@{m.GetProperty("hostname").GetString()}, cwd {m.GetProperty("cwd").GetString()}
                  files     {regular.Count} files, {files.Count - regular.Count} directories, {bytes} bytes
                  session   {Count("variables")} variables, {Count("functions")} functions, {Count("history")} history entries

                """ + (history == null ? "" : $"  history   {history.Calls.Count} calls, {1 + history.Calls.Count(c => c.SnapshotAfter != null)} checkpoints\n");
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            throw new CliException(e is FileNotFoundException ? $"{file}: no such file" : e.Message);
        }
    }
}
