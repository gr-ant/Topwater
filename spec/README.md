# Command spec

One description of every built-in command, shared by the three implementations. The behaviour itself is pinned by
the recorded cases in [`tests/corpus`](../tests/corpus/README.md); this directory holds the parts each port used to
spell out on its own: the command list, summaries, `--help` and `--version` text, option tables, usage-error format
and diagnostic wording.

```
spec/
  command.schema.json      JSON Schema for one command file
  commands/<name>.json     one file per command (. : [ are spelled dot colon bracket)
  shell.json               shared defaults, getopt and errno strings, quoting rules, the shell's own diagnostics
  shell.schema.json        JSON Schema for shell.json
tools/spec-gen/gen.py      generator: validates spec/ against the schemas, writes each port's tables
tools/spec-gen/test_gen.py checks the validation, and that every message a port looks up exists and every shell
                           diagnostic is used by all three ports
```

The generator writes, and the repository checks in:

| Port | Generated file | Used through |
|---|---|---|
| C# | `src/Topwater/Generated/CommandSpec.g.cs` | `CommandSpec.Of(name)`, `CommandSpec.Message(...)`, `CommandSpec.SubMessage(name, sub, ...)`, `Args.Parse(args, CommandSpec.Of(name))`, `CommandSpec.Diagnostics.X` / `CommandSpec.Format(...)`, `CommandSpec.Errno.X` (`src/Topwater/CommandSpec.cs`) |
| Python | `python/src/topwater/_spec.py` | `COMMANDS[name]`, `spec_message(...)`, `spec_sub_message(name, sub, ...)`, `Args.parse(args, COMMANDS[name])` (`commands/__init__.py`), `diagnostic(...)`, `DIAGNOSTICS`, `ERRNO` |
| TypeScript | `typescript/src/spec.generated.ts` | `specOf(name)`, `specMessage(...)`, `specSubMessage(name, sub, ...)`, `Args.parse(args, specOf(name))` (`src/command.ts`), `diagnostic(...)`, `DIAGNOSTICS`, `ERRNO` |

Nothing reads `spec/` at run time: no file IO, no new dependencies, and the browser bundle needs nothing extra.

## A command file

```json
{
  "$schema": "../command.schema.json",
  "name": "head",
  "category": "text",
  "summary": "Print the first lines of files",
  "parser": "args",
  "options": [
    {"short": "n", "long": ["lines"], "arg": "NUM", "description": "show NUM lines from the start (negative NUM: everything except the last NUM)"},
    {"short": "q", "long": ["quiet", "silent"], "description": "never print file name headers"}
  ],
  "messages": {
    "openFailed": "cannot open {file:shell-always} for reading: {errno}",
    "readDirectory": "error reading {file:shell-always}: {errno}"
  }
}
```

| Field | Meaning |
|---|---|
| `name`, `category`, `summary` | `help` lists `summary`; the default `--help` is `Usage: NAME [OPTION]... [ARG]...` then `SUMMARY.` |
| `ports` | the ports that have the command, when not all three (`bc` is C# only) |
| `shellBuiltin` | part of bash itself: `type` says "shell builtin", errors get the `bash: ` prefix |
| `programFile` | `false` for builtins that have no `/usr/bin/NAME` on a real system |
| `standardOptions` | `false` when a lone `--help` / `--version` is just an argument (`echo --help`) |
| `version`, `help` | what `--version` and `--help` print, when not the defaults in `shell.json`; `help NAME` prints `help` when given |
| `reportsAs` | the name in usage errors, when it differs (`egrep` reports as `grep`) |
| `usageExitCode`, `tryHelp` | the exit status and the lines after a usage error (`grep` adds its `Usage:` line) |
| `parser` | `args`: the shared getopt-style parser reads `options`, which the generator turns into each port's table. `custom`: the command parses its own arguments and `options` is documentation |
| `options[]` | `short`, `long` (aliases), `arg` (present when it takes a value), `description`, `ports` (when not all), `key` (what the code looks it up by: for a long-only option a control character, e.g. tar's `--strip-components` uses `\u0001`, so that no short letter is accepted by accident), `custom` (the command reads it itself before the shared parser, e.g. grep's `-e`), `ignored` (accepted but without effect: `true`, or the ports where it does nothing) |
| `exitCodes`, `notes` | documentation |
| `messages` | diagnostic templates keyed by an id the code uses; the port adds the `NAME: ` prefix |
| `subcommands` | for a command with subcommands (`git`): name → `summary`, `usage` (lines), `help` (default: generated from usage, summary and options), `options` (same shape as above; always parsed through the generated table), `messages` (falling back to the command's, then the defaults), `ports`, `unsupported` (recognised, answered with the command's own message and a failure). The command itself uses `parser: custom` and dispatches |

The schema in `command.schema.json` is the full list; the generator rejects unknown keys.

### Templates and quoting

`{x}` is the value as given. File names in GNU messages are quoted the way coreutils quotes them under
`LANG=C.UTF-8`, and values such as an invalid date in the locale's curly quotes:

| Placeholder | Result |
|---|---|
| `{file}` | `a b` (as given; grep and sed print names this way) |
| `{file:shell}` | `nope`, `'a b'`: quoted only when it needs it |
| `{file:shell-always}` | `'nope'`, `"it's"`, `'a'$'\t''b'`: always quoted |
| `{value:locale}` | `‘garbage’` |

`shell.json` spells out the exact rules (`quoting`), the shared `errno` strings, the getopt messages, the shell's own
`diagnostics` (syntax errors, readonly and identifier errors, arithmetic errors, command not found, ...: plain
`{name}` placeholders only, without the `bash: line N: ` prefix the shell adds) and the defaults: a command without its
own `openFailed` reports an unreadable file as `{file:shell}: {errno}`, and generic builtin messages (`notFound`,
`numericArgument`, ...) live in `defaults.messages`.

Summaries, help texts and option descriptions are our own words about what Topwater does, short and factual; never
copy a real program's `--help`, `help` or manual text (the project is clean-room). An option Topwater accepts but does
nothing with says "accepted and ignored" and carries `"ignored"`. `ports` says which implementations accept it; the
corpus is the record of what each does. What a real program *prints* when it runs (messages, version lines) is
behaviour and is matched.

## Adding or changing a command

**Spec first, always.** A new command, subcommand, option or message starts as JSON in `spec/` and is regenerated
before any port's code changes. No port hard-codes a summary, help text, option table or message. If the schema
cannot express something a command needs (subcommands, an argument shape), extend `command.schema.json` and
`gen.py` first, then write the command against it.

1. Write or edit `spec/commands/<name>.json`. List only options Topwater accepts, with `ports` for the ones a port
   lacks (or `custom: true` for one the command reads itself before the shared parser), and describe each in your own
   words. Check behaviour against real ubuntu:24.04 with `tests/corpus/record.py`.
2. `python3 tools/spec-gen/gen.py` regenerates the three tables. CI runs `--check`, which fails when they are stale or
   the spec is invalid.
3. Implement the command in each port and register it with no summary (`shell.Register("name", handler)`,
   `r("name", handler)`, `reg('name', handler)`): built-ins take their summary and help from the spec. Parse
   arguments with the spec's table (`Args.Parse(ctx.Args, CommandSpec.Of("name"))` and the Python and TypeScript
   equivalents) and take message wording from `messages`.
4. Record its behaviour in the corpus (`tests/corpus/record.py`) and run the three suites. Each port's drift test
   (`CommandSpecTests.cs`, `test_command_spec.py`, `command-spec.test.ts`) checks that the built-ins are exactly the
   spec's, that `--help`, `--version` and the summaries match, that every option listed for the port is accepted,
   that every other letter and every option listed only for other ports is rejected, and that a bad option gives the
   spec's usage error. `python3 tools/spec-gen/gen.py --check` also validates every file against the schemas
   (`command.schema.json`, `shell.schema.json`), and `python3 tools/spec-gen/test_gen.py` checks that every message key
   the code asks for exists (a missing one would only fail on the error path).

When two ports disagree, real bash decides: record the disputed point with `record.py` (the `command-spec` area holds
the ones found while building this spec) and fix the port that is wrong.

## Known gaps

- `bc` is C# only.
- Some options only TypeScript accepts; they are marked `"ports": ["ts"]` (for example `uniq -f -s -w`,
  `sort -m -S -T`, `wc -L`, `mkdir -m`, `tree -f -F -i`, `nl -n -v -i`, `cp -i -t -T`, `rm -i`, `du -b`, `uname -i -p`,
  `xargs -i --eof`).
- The `custom`-parser commands other than `sed`, `env`, `hash`, `umask`, `unalias` and `git` (`find`, `awk`, `echo`,
  `printf`, `set`, ...) have no option list yet.
- Still spelled inline in each port: `jq`, `awk` and `bc` (their own language errors), the regex translator's errors
  shared by grep and sed, the script-file and `#!` errors, the `[[ =~ ]]` bad-regex message, the filesystem's
  snapshot-loading messages, and the messages below.
- Messages the ports word differently, so still inline until real bash settles them: `diff DIR FILE`, `chgrp`'s
  permission error ("changing ownership" vs "changing group"), `to-markdown x.pdf`, `tar --strip-components=x`, and
  `unzip`'s per-entry decode errors.
