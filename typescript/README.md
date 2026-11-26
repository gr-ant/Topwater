# Topwater for TypeScript

One `bash` tool for your LLM agent, backed by a simulated Linux machine that runs inside your Node process or in the
browser. No runtime dependencies. The same code runs in Node 22.3 or later and in browsers.

This page covers the TypeScript API. The [main README](https://github.com/gr-ant/Topwater/blob/main/README.md) explains what Topwater is, and
[docs/](https://github.com/gr-ant/Topwater/blob/main/docs/README.md) is the reference; its samples are C#, and this package follows it except where
[noted below](#differences-from-the-c-version).

- [Install](#install)
- [Quickstart](#quickstart)
- [Agent frameworks](#agent-frameworks)
- [Deterministic runs, snapshots and recordings](#deterministic-runs-snapshots-and-recordings)
- [Differences from the C# version](#differences-from-the-c-version)
- [In the browser](#in-the-browser)
- [Layout and tests](#layout-and-tests)

## Install

Not on npm yet. Build it from the repository; the package is an ES module:

```bash
git clone https://github.com/gr-ant/Topwater
(cd Topwater/typescript && npm install && npm run build)
npm install ./Topwater/typescript
```

## Quickstart

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
| `shell.AllowWeb(new WebPolicy { ... })` | `shell.allowWeb({ domains, methods, credentials, ... })`: [web access](https://github.com/gr-ant/Topwater/blob/main/docs/web.md) for `curl` and `wget` |
| `BashTool.ExecuteAsync(shell, cmd)` | `BashTool.execute(shell, cmd)` |
| `BashTool.DescriptionFor(shell)` | `BashTool.descriptionFor(shell)` |

A command handler gets a `CommandContext`: `args`, `stdout`/`stderr` writers, `stdinText`, `readLine()`, `fs`,
`resolvePath()`, `checkTime()` (call it in long loops so the call's time limit can stop you) and `invoke()`. Throw
`ShellError(message, exitCode)` for a `name: message` error; any other exception becomes `name: message`, exit 1.

## Agent frameworks

Optional adapters (separate entry points; the core has no dependencies). Install the SDK you use: `npm i ai`,
`npm i @openai/agents zod`, `npm i langchain @langchain/core`, `npm i @mastra/core zod` or
`npm i @anthropic-ai/claude-agent-sdk @modelcontextprotocol/sdk zod`. Each returns the same `bash` tool wired to a
`Shell`; calls on one shell run one at a time, whichever adapter makes them. The adapters are tested with ai 7,
@openai/agents 0.22, @langchain/core 1.5, @mastra/core 1.86, @modelcontextprotocol/sdk 1.32 and Claude Agent SDK 0.3;
they are not declared as peer dependencies, so other versions install but are untested.

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

LangChain.js agents (`createAgent`, built on LangGraph) and LangGraph's `ToolNode` take the LangChain tool:

```ts
import { bashTool } from 'topwater/langchain';
import { createAgent } from 'langchain';

const agent = createAgent({ model: 'anthropic:claude-sonnet-4-5', tools: [bashTool(new Shell())] });
await agent.invoke({ messages: [{ role: 'user', content: '...' }] });
```

```ts
import { bashTool } from 'topwater/mastra';
import { Agent } from '@mastra/core/agent';

const agent = new Agent({ id: 'assistant', name: 'Assistant', instructions: 'Use bash to ...', model, tools: { bash: bashTool(new Shell()) } });
await agent.generate('...');
```

The Claude Agent SDK takes tools from in-process MCP servers. `bashOptions` returns `query` options whose only tool is
the shell (`mcp__topwater__bash`): it turns off Claude Code's own tools, whose Bash runs on the host, and keeps the
developer's Claude Code settings, allow rules and MCP servers out of the session (`settingSources: []`,
`strictMcpConfig: true`). `bashMcpServer` is the server alone.

```ts
import { bashOptions } from 'topwater/claude-agent-sdk';
import { query } from '@anthropic-ai/claude-agent-sdk';

for await (const message of query({ prompt: '...', options: bashOptions(new Shell()) })) if (message.type === 'result') console.log(message);
```

## Deterministic runs, snapshots and recordings

Machines and recordings are [`.topwater` files](https://github.com/gr-ant/Topwater/blob/main/docs/snapshots-and-recordings.md#the-file-format), the same in
every implementation, so they move freely between them:

```ts
import { Shell, ShellSnapshot, Recording, startRecording } from 'topwater';

const shell = new Shell({ startTime: Date.UTC(2026, 10, 25, 9), randomSeed: 1 }); // virtual clock (sleep is instant) and seeded $RANDOM
const file = await ShellSnapshot.capture(shell).save();                          // a .topwater without history: the machine
(await ShellSnapshot.load(file)).restore(new Shell());                           // checked as untrusted, with the same limits

const { recording, stop } = startRecording(shell, { metadata: { task: 'demo' }, snapshotEvery: 5 });
await shell.run('ls');
stop();
const bytes = await recording.save();                     // .topwater: the end machine, history.jsonl and checkpoints
const again = await Recording.load(bytes);                // also any implementation's .topwater, snapshot zip or old .jsonl recording
const afterTurn3 = await again.shellAt(2);                // nearest checkpoint, then re-runs the calls after it
console.log(again.calls[0].files, (await again.replay()).differences);
```

A recording's `manifest.json` is the machine after the last call, so `ShellSnapshot.load` opens it as that machine.
Each call records the shell's clock when it started (`time`) and the paths it added, changed or removed (`files`). A
deterministic recording made in another implementation re-runs here with the same times, `$RANDOM` values and `mktemp`
names; where the implementations' commands differ, so does the output.

## Differences from the C# version

- Not ported yet: `bc`, `Fork`, real host programs (`RegisterHostCommand`), containers, replaying a recording as a
  test or on real bash in Docker (`Topwater.Replay`), and the `[Command]` attribute binder (use `registerTools` with a
  JSON Schema instead).
- A single JavaScript regular-expression match cannot be interrupted. In Node, loading the package sets V8's
  `--enable-experimental-regexp-engine-on-excessive-backtracks` flag (process-wide; it changes speed, never results),
  so a catastrophically backtracking match switches to V8's linear-time engine instead of hanging. Patterns with
  backreferences have no linear engine: one such match can still overrun the call's time limit (the C# version caps
  one match at 5 s). Run the shell in a worker thread (in a browser, a Web Worker; see below) if you need a hard stop.
- `zip` and `gzip -v` report the ratio of the platform's deflate (`CompressionStream`), which differs slightly from
  Info-ZIP's; compression levels (`-1`..`-9`) are accepted but ignored.
- `shell.fs` is your app's unrestricted view of the files; the shell and its commands work through a view that acts as
  the agent (`shell.fs.as('agent')` gives you one too). The C# version tracks this with an async-local value; here
  whose call it is travels with the view object, so your code is unrestricted even while an agent command awaits, and
  a command handler's `ctx.fs` is always the agent's view.

## In the browser

The hosted [demo](https://gr-ant.github.io/Topwater/) and [replay viewer](https://gr-ant.github.io/Topwater/replay.html)
are this build. `npm run build:browser` writes:

- `dist-browser/topwater.js`: the library as one minified ES module (same API as the Node package; no Node imports).
- `dist-browser/worker.js`: a Web Worker that runs a shell; drive it with `TopwaterSession` from `browser/session.ts`.
- `browser/dist/`: the demo, a folder of static files. Serve it from any static host
  (`python3 -m http.server -d browser/dist`); it makes no requests outside its own folder. `index.html` is a terminal
  and `replay.html` is the recording viewer.

The viewer opens a `.topwater` file or an old `.jsonl` recording (or the bundled sample, recorded in .NET). Step
through its turns and see each turn's output, the files as they were after it and what it changed, or compare any
two moments. "Continue from here" opens a terminal in the page on the machine as of that turn; what you type forms a
branch of the timeline that you can compare with the others and download as its own `.topwater` file. "Open in a new
tab" hands the machine to `index.html?fork=...` through `localStorage` instead.

A turn's files come from the recording's checkpoint after it when there is one. Otherwise the viewer's worker
restores the nearest earlier checkpoint and re-runs the calls in between with this implementation, and says whether
each re-run printed what was recorded. For a recording made in another implementation it may not: they differ in
places.

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

## Layout and tests

| File | What |
|---|---|
| `src/parser.ts` | recursive-descent parser for the shell language (words, expansions, heredocs, compound commands) |
| `src/shell.ts` | the interpreter: expansion, redirections, pipelines, functions, traps, limits, the `Shell` API |
| `src/vfs.ts` | the in-memory filesystem, permissions, the disk cap |
| `src/arith.ts`, `src/glob.ts`, `src/regex.ts`, `src/format.ts`, `src/bytes.ts` | arithmetic, patterns, POSIX regex, printf/strftime, binary-safe text |
| `src/commands/*.ts` | built-ins and programs (`builtins`, `files`, `text`, `sed`, `awk`, `jq*`, `binary`, `archive`, `git`, `gitcore`) |
| `src/inflate.ts`, `src/compress.ts`, `src/md5.ts`, `src/sha1.ts` | a synchronous inflate, compression via `CompressionStream` and a small synchronous zlib for git, MD5, SHA-1 (no `node:zlib`/`node:crypto`) |
| `src/tools.ts` | schema-described tools and MCP mounting |
| `src/snapshot.ts`, `src/recording.ts` | machines and recordings as `.topwater` files (and loading the older recording format) |
| `browser/` | the Web Worker host (`worker.ts`), the page-side `TopwaterSession` (`session.ts`), the demo pages (`demo/`: the terminal and the recording viewer) and the viewer's worker (`replay-worker.ts`, `diff.ts`) |
| `test/corpus.test.ts` | runs the shared cases in `../tests/corpus` (recorded from real bash and GNU tools) |

`npm test` runs everything, and `npm run typecheck` checks the types. The shared cases live in
[`../tests/corpus`](https://github.com/gr-ant/Topwater/blob/main/tests/corpus/README.md), not here; known differences are listed with reasons in
`../tests/corpus/skip/typescript.json`.
