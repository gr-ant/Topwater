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

## Vercel AI SDK and OpenAI Agents

Optional adapters (separate entry points; the core has no dependencies). Install the SDK you use: `npm i ai` or
`npm i @openai/agents zod`. Both return the `bash` tool wired to a `Shell`; calls through one adapter run one at a time.

```ts
import { Shell } from 'topwater';
import { bashTool } from 'topwater/ai-sdk';
import { generateText, stepCountIs } from 'ai';

const shell = new Shell();
await generateText({ model, tools: { bash: bashTool(shell) }, stopWhen: stepCountIs(10), prompt: '...' });
```

```ts
import { Shell } from 'topwater';
import { bashTool } from 'topwater/openai-agents';
import { Agent, run } from '@openai/agents';

const shell = new Shell();
const agent = new Agent({ name: 'Assistant', instructions: 'Use bash to ...', tools: [bashTool(shell)] });
await run(agent, '...');
```

## Layout

| File | What |
|---|---|
| `src/parser.ts` | recursive-descent parser for the shell language (words, expansions, heredocs, compound commands) |
| `src/shell.ts` | the interpreter: expansion, redirections, pipelines, functions, traps, limits, the `Shell` API |
| `src/vfs.ts` | the in-memory filesystem, permissions, the disk cap |
| `src/arith.ts`, `src/glob.ts`, `src/regex.ts`, `src/format.ts`, `src/bytes.ts` | arithmetic, patterns, POSIX regex, printf/strftime, binary-safe text |
| `src/commands/*.ts` | built-ins and programs (`builtins`, `files`, `text`, `sed`, `awk`, `jq*`, `binary`, `archive`) |
| `src/tools.ts` | schema-described tools and MCP mounting |
| `test/corpus.test.ts` | runs the shared cases in `../tests/corpus` (recorded from real bash and GNU tools) |

`npm test` runs everything. The shared cases live in `../tests/corpus`, not here; known differences are listed with reasons in `../tests/corpus/skip/typescript.json`.

## Differences from the C# version

- Not ported yet: `bc`, snapshots (`Snapshot`/`Restore`/`Fork`/`Save`/`Load`), real host programs (`RegisterHostCommand`), the `[Command]` attribute binder (use `registerTools`
  with a JSON Schema instead), and the Microsoft.Extensions.AI adapter.
- A single JavaScript regular-expression match cannot be interrupted. Loading the package sets V8's
  `--enable-experimental-regexp-engine-on-excessive-backtracks` flag (process-wide; it changes speed, never results),
  so a catastrophically backtracking match switches to V8's linear-time engine instead of hanging. Patterns with
  backreferences have no linear engine: one such match can still overrun the call's time limit (the C# version caps
  one match at 5 s). Run the shell in a worker thread if you need a hard stop.
- `to-markdown` does not read PDF (the C# version shells out to `pdftotext`; nothing here touches the host).
- `zip` reports zlib's compression ratio, which differs slightly from Info-ZIP's.
- The app's own filesystem calls stay unrestricted while an agent command runs (via `AsyncLocalStorage`), as in C#.
