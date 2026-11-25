# Topwater for TypeScript

A port of [Topwater](../README.md) to TypeScript/Node: a simulated bash shell for LLM agents. An in-process bash
interpreter, an in-memory Ubuntu-like filesystem with users and permissions, about 110 reimplemented commands, and
resource limits, with no runtime dependencies (Node built-ins only). `../docs/reference.md` describes the behaviour;
this package follows it except where noted below.

```ts
import { Shell, BashTool } from 'topwater';

const shell = new Shell();
shell.fs.writeText('/home/agent/notes.txt', 'one\ntwo\n');
shell.register('deploy', async ctx => { ctx.stdout.write(`deploying ${ctx.args[0]}\n`); return 0; }, 'Deploy a service');

const r = await shell.run('wc -l notes.txt && deploy api');
console.log(r.stdout, r.stderr, r.exitCode);

// one tool for the model: BashTool.name, BashTool.description, BashTool.inputSchema
const toolResult = await BashTool.execute(shell, 'ls -la');
```

| C# | TypeScript |
|---|---|
| `new Shell(new ShellOptions { ... })` | `new Shell({ maxCallDurationMs, maxFileSystemBytes, maxStringBytes, maxLoopIterations, user, hostname, ... })` |
| `await shell.RunAsync(cmd)` | `await shell.run(cmd)` → `ShellResult { stdout, stderr, exitCode, trailer, toToolOutput() }` |
| `shell.Register(name, handler, description)` | `shell.register(name, ctx => number \| void \| Promise<...>, description, usage?)` |
| `shell.Fs.WriteAllText / WriteAllBytes` | `shell.fs.writeText / writeBytes` (also `readText`, `mkdir`, `chmod`, `setOwner`, ...) |
| `shell.RegisterTools(program, specs, invoke)` | `shell.registerTools(program, specs, (tool, args) => ({ text, isError }))` |
| `shell.MountMcpAsync(program, client)` | `await shell.mountMcp(program, client, { include })` with any MCP client that has `listTools`/`callTool` (the official SDK `Client` fits) |
| `BashTool.ExecuteAsync(shell, cmd)` | `BashTool.execute(shell, cmd)` |

A command handler gets a `CommandContext`: `args`, `stdout`/`stderr` writers, `stdinText`, `readLine()`, `fs`,
`resolvePath()`, `checkTime()` (call it in long loops so the call's time limit can stop you) and `invoke()`. Throw
`ShellError(message, exitCode)` for a `name: message` error; any other exception becomes `name: message`, exit 1.

## Layout

| File | What |
|---|---|
| `src/parser.ts` | recursive-descent parser for the shell language (words, expansions, heredocs, compound commands) |
| `src/shell.ts` | the interpreter: expansion, redirections, pipelines, functions, traps, limits, the `Shell` API |
| `src/vfs.ts` | the in-memory filesystem, permissions, the disk cap |
| `src/arith.ts`, `src/glob.ts`, `src/regex.ts`, `src/format.ts`, `src/bytes.ts` | arithmetic, patterns, POSIX regex, printf/strftime, binary-safe text |
| `src/commands/*.ts` | built-ins and programs (`builtins`, `files`, `text`, `sed`, `awk`, `jq*`, `binary`, `archive`) |
| `src/tools.ts` | schema-described tools and MCP mounting |
| `test/compat.test.ts` | the recorded tables from the C# tests (`test/data`, made by `scripts/extract-tests.mjs`) |

`npm test` runs everything; `node scripts/extract-tests.mjs` refreshes `test/data` from `../tests/Topwater.Tests`.

## Differences from the C# version

- Not ported yet: real host programs (`RegisterHostCommand`), the `[Command]` attribute binder (use `registerTools`
  with a JSON Schema instead), and the Microsoft.Extensions.AI adapter.
- A single JavaScript regular-expression match cannot be interrupted. Loading the package sets V8's
  `--enable-experimental-regexp-engine-on-excessive-backtracks` flag (process-wide; it changes speed, never results),
  so a catastrophically backtracking match switches to V8's linear-time engine instead of hanging. Patterns with
  backreferences have no linear engine: one such match can still overrun the call's time limit (the C# version caps
  one match at 5 s). Run the shell in a worker thread if you need a hard stop.
- `to-markdown` does not read PDF (the C# version shells out to `pdftotext`; nothing here touches the host).
- `zip` reports zlib's compression ratio, which differs slightly from Info-ZIP's.
- The app's own filesystem calls stay unrestricted while an agent command runs (via `AsyncLocalStorage`), as in C#.
