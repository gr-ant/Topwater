# Topwater for TypeScript

A port of [Topwater](../README.md) to TypeScript/Node: a simulated bash shell for LLM agents. An in-process bash
interpreter, an in-memory Ubuntu-like filesystem with users and permissions, about 110 reimplemented commands, and
resource limits, with no runtime dependencies. The same code runs in Node 22+ and in browsers (see below). `../docs/reference.md` describes the behaviour;
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

## Deterministic runs, snapshots and recordings

These use the C# version's formats, so files move freely between the two:

```ts
import { Shell, ShellSnapshot, Recording, startRecording } from 'topwater';

const shell = new Shell({ startTime: Date.UTC(2026, 10, 25, 9), randomSeed: 1 }); // virtual clock (sleep is instant) and seeded $RANDOM
const zip = await ShellSnapshot.capture(shell).save();                           // the snapshot zip (docs/reference.md "Snapshots")
(await ShellSnapshot.load(zip)).restore(new Shell());                            // checked as untrusted, with the same limits

const { recording, stop } = startRecording(shell, { metadata: { task: 'demo' }, snapshotEvery: 5 });
await shell.run('ls');
stop();
const jsonl = await recording.save();                     // JSON Lines (docs/reference.md "Recordings")
const again = await Recording.load(jsonl);                // also reads recordings made in .NET
const afterTurn3 = await again.shellAt(2);                // nearest snapshot, then re-runs the calls after it
console.log((await again.replay()).differences);
```

A deterministic recording made in .NET re-runs here with the same times, `$RANDOM` values and `mktemp` names; where
the two ports' commands differ, so does the output.

## Layout

| File | What |
|---|---|
| `src/parser.ts` | recursive-descent parser for the shell language (words, expansions, heredocs, compound commands) |
| `src/shell.ts` | the interpreter: expansion, redirections, pipelines, functions, traps, limits, the `Shell` API |
| `src/vfs.ts` | the in-memory filesystem, permissions, the disk cap |
| `src/arith.ts`, `src/glob.ts`, `src/regex.ts`, `src/format.ts`, `src/bytes.ts` | arithmetic, patterns, POSIX regex, printf/strftime, binary-safe text |
| `src/commands/*.ts` | built-ins and programs (`builtins`, `files`, `text`, `sed`, `awk`, `jq*`, `binary`, `archive`) |
| `src/inflate.ts`, `src/compress.ts`, `src/md5.ts` | a synchronous inflate, compression via `CompressionStream`, MD5 (no `node:zlib`/`node:crypto`) |
| `src/tools.ts` | schema-described tools and MCP mounting |
| `src/snapshot.ts`, `src/recording.ts` | snapshots and recordings in the C# version's file formats |
| `browser/` | the Web Worker host (`worker.ts`), the page-side `TopwaterSession` (`session.ts`), the demo pages (`demo/`: the terminal and the recording viewer) and the viewer's worker (`replay-worker.ts`, `diff.ts`) |
| `test/corpus.test.ts` | runs the shared cases in `../tests/corpus` (recorded from real bash and GNU tools) |

`npm test` runs everything. The shared cases live in `../tests/corpus`, not here; known differences are listed with reasons in `../tests/corpus/skip/typescript.json`.

## Differences from the C# version

- Not ported yet: `bc`, `Fork`, real host programs (`RegisterHostCommand`), the `[Command]` attribute binder (use `registerTools`
  with a JSON Schema instead), and the Microsoft.Extensions.AI adapter.
- A single JavaScript regular-expression match cannot be interrupted. In Node, loading the package sets V8's
  `--enable-experimental-regexp-engine-on-excessive-backtracks` flag (process-wide; it changes speed, never results),
  so a catastrophically backtracking match switches to V8's linear-time engine instead of hanging. Patterns with
  backreferences have no linear engine: one such match can still overrun the call's time limit (the C# version caps
  one match at 5 s). Run the shell in a worker thread (in a browser, a Web Worker; see below) if you need a hard stop.
- `to-markdown` does not read PDF (the C# version shells out to `pdftotext`; nothing here touches the host).
- `zip` and `gzip -v` report the ratio of the platform's deflate (`CompressionStream`), which differs slightly from
  Info-ZIP's; compression levels (`-1`..`-9`) are accepted but ignored.
- `shell.fs` is your app's unrestricted view of the files; the shell and its commands work through a view that acts as
  the agent (`shell.fs.as('agent')` gives you one too). The C# version tracks this with an async-local value; here
  whose call it is travels with the view object, so your code is unrestricted even while an agent command awaits, and
  a command handler's `ctx.fs` is always the agent's view.

## In the browser

`npm run build:browser` writes:

- `dist-browser/topwater.js`: the library as one minified ES module (same API as the Node package; no Node imports).
- `dist-browser/worker.js`: a Web Worker that runs a shell; drive it with `TopwaterSession` from `browser/session.ts`.
- `browser/dist/`: the demo, a folder of static files. Serve it from any static host
  (`python3 -m http.server -d browser/dist`); it makes no requests outside its own folder. `index.html` is a terminal;
  `replay.html` is the recording viewer: open a `.jsonl` recording (or the bundled sample, recorded in .NET), step
  through its turns, see each turn's output, the files as they were after it and a diff of what it changed, and open
  the terminal on the machine as of any turn ("Open a terminal here", which hands the snapshot over through
  `localStorage` to `index.html?fork=...`). A turn's files come from the recording's snapshot after it when there is
  one; otherwise the viewer's worker restores the nearest earlier snapshot and re-runs the calls in between with this
  port, and says whether each re-run printed what was recorded.

Run the shell in a worker, not on the page's thread: one JavaScript call (a catastrophically backtracking regex, a long
synchronous loop inside a command) can't be interrupted from inside, and browsers have no regex-engine fallback flag.
`TopwaterSession` gives every call `maxCallDurationMs` plus a grace period; past that it terminates the worker and
starts a new one from the snapshot the worker sent after the last finished call (files under `/home`, `/tmp` and
`/var/tmp` with their modes, owners and times, plus cwd, variables, aliases, functions and history; arrays and
`declare` attributes are not kept). If the files exceed 32 MB there is no snapshot and the session is reset instead.

What a browser doesn't have:

- No processes: host programs and stdio MCP servers can't exist. Tools you register in JavaScript
  (`register`, `registerTools`, an in-page MCP client via `mountMcp`) work as in Node.
- `registerApi` uses `fetch`, so it can only reach APIs that allow your page's origin (CORS), and any credential you
  give it lives in the page. Prefer a proxy you control.
- `npm run test:browser` builds the demo and checks it in headless Chromium (Playwright).
