# Source layout

Topwater has three ports, C# (`src/Topwater/`), Python (`python/src/topwater/`) and TypeScript (`typescript/src/`),
and they should be laid out the same way: the same files (in each language's casing), the same split of
responsibilities, the same order of sections in each file and, where the language allows, the same names for the
main functions. This page is the map. `tools/layout.json` will be its machine-readable copy, checked in CI (§8).

**Status: in progress.** Work through §9 one phase at a time and tick off each phase here as it lands (a phase is
ticked when all three ports have it; the column says which ports have it). C# has done phases 1 and 4 to 6 and is
strict in the checker (no `legacy` entry).

| Phase | What | Done |
|---|---|---|
| 0 | `tools/layout.json` (the target layout, frozen) and `tools/check_layout.py`; tag `pre-layout` (df9444e, the last commit before any move) | yes |
| 1 | Shell split, C# (the pilot: §3.2 is corrected from what it finds before phases 2 and 3 start) | C# |
| 2 | Shell split, Python | |
| 3 | Shell split, TypeScript | |
| 4 | git split, Python and TypeScript (to match C#); C# section names aligned | C# |
| 5 | Command grab-bags split by domain, all three ports | C# |
| 6 | Engine extractions: `Sed`, `Find`, `Diff`, `Format`; TS `convert.ts` out of `commands/documents.ts` | C# |
| 7 | Optional: parser AST, jq | |

## 1. Rules for every move

- **Moves only.** A move commit changes no behaviour and no logic. The only edits allowed are imports and usings,
  re-exports, access modifiers (TS `private` → `/** @internal */`), the TS `this: Shell` parameter and return
  types `tsc` asks for (§3.3), section markers, and the registrar calls needed to wire up a new file. Renames go in separate commits (§4.3).
- **Public API and import paths stay the same.** `topwater.shell`, `./shell.js`, `Topwater.Shell` and every name
  imported from them today keep working through re-exports.
- **Keep the current order where the ports agree.** All three shell files already share their section order
  (registration → execution → variables → arrays → expansion), so most cuts are contiguous blocks.
- **Where the ports' current orders differ, Python's is the reference.** C# and TS members are reordered to it inside
  the move commit (member order means nothing to either compiler). Python keeps its order except where §3.2 says so.
- **§3.2, §5 and `tools/layout.json` are the contract.** An implementer does not edit them. A symbol the tables
  don't list goes where its twin in another port is listed (same root name, §4.2); with no twin, next to its only
  caller. Report every such symbol, and anything in the tables that turns out wrong, instead of deciding alone: the
  tables are corrected once, for all three ports.
- **Each phase is one commit per port** and passes, unchanged, the test rows in AGENTS.md (the suites include
  `tests/corpus`) plus `npm run build:browser && npm run test:browser`. No test file changes in a move commit.

## 2. Module map (target)

A dash means the port has no such module; §8 records why.

### Shell and core

| Role | C# `src/Topwater/` | Python `python/src/topwater/` | TS `typescript/src/` |
|---|---|---|---|
| Shell: state, constructor, machine seeding, clock, registration, `Run`, limits | `Shell/Shell.cs` | `shell/__init__.py` | `shell.ts` |
| Shell: shared types (options, result, context, arrays, signals, `Io`) | `Shell/Types.cs` | `shell/types.py` | `shell/types.ts` |
| Shell: scripts, lists, pipelines, compound commands, jobs, traps, `[[ ]]` | `Shell/Execution.cs` | `shell/execution.py` | `shell/execution.ts` |
| Shell: redirections, fds, output writers | `Shell/Redirection.cs` | `shell/redirection.py` | `shell/redirection.ts` |
| Shell: simple commands, `Invoke`, PATH and hash lookup, scripts, functions | `Shell/Invocation.cs` | `shell/invocation.py` | `shell/invocation.ts` |
| Shell: options, scalars, attributes, arrays | `Shell/Variables.cs` | `shell/variables.py` | `shell/variables.ts` |
| Shell: word expansion, parameters, arithmetic, substitution, splitting | `Shell/Expansion.cs` | `shell/expansion.py` | `shell/expansion.ts` |
| Shell: snapshot, fork, save and load, session state, start of a recording | `Shell/Session.cs` | `shell/session.py` | `shell/session.ts` |
| Brace expansion and globbing | `Glob.cs` | `glob.py` | `glob.ts` |
| printf, strftime, quoting and escapes engine | `Format.cs` | `format.py` | `format.ts` |
| Parser (lexer, parser, AST) | `Parsing/Parser.cs` | `parser.py` | `parser.ts` |
| Arithmetic | `Arith.cs` | `arith.py` | `arith.ts` |
| Virtual file system | `VirtualFileSystem.cs` | `vfs.py` | `vfs.ts` |
| Snapshot format | `ShellSnapshot.cs` | `snapshot.py` | `snapshot.ts` |
| Recording and replay | `Recording.cs` | `recording.py` | `recording.ts` |
| Web access (policy, session, transport) | `WebAccess.cs` | `webaccess.py` | `web.ts` |
| Command spec tables (generated) | `CommandSpec.cs`, `Generated/CommandSpec.g.cs` | `_spec.py` | `spec.generated.ts` |
| Command API helpers (context, args, inputs, messages) | `Builtins/Builtins.cs` | `commands/__init__.py` | `command.ts` |
| Byte-as-text encoding | `ByteText.cs` | `bytetext.py` | `bytes.ts` |
| Schema-described tools as one program | `ToolCommands.cs` | `tools.py` | `tools.ts` |
| HTTP APIs as one program | `ApiCommand.cs` | `api.py` | `api.ts` |
| The `bash` tool definition | `BashTool.cs` | `__init__.py` (`TOOL_*`) | `tool.ts` |
| Office/HTML → Markdown | `MarkdownConverter.cs` | `convert.py` | `convert.ts` (phase 6; now inside `commands/documents.ts`, which keeps the `to-markdown` command and re-exports `toMarkdown`, `addDocument`) |

### Commands (`Builtins/`, `commands/`, `commands/`)

| Role | C# `Builtins/` | Python `commands/` | TS `commands/` |
|---|---|---|---|
| Registry: `RegisterAll` | `Builtins.cs` | `__init__.py` | `index.ts` |
| Scripting builtins (§5) | `ScriptingCommands.cs` | `scripting.py` | `scripting.ts` |
| Variable builtins | `VariableCommands.cs` | `variables.py` | `variables.ts` |
| Processes and time | `ProcessCommands.cs` | `process.py` | `process.ts` |
| System identity and packages | `SystemCommands.cs` | `system.py` | `system.ts` |
| Directories and path names | `PathCommands.cs` | `paths.py` | `paths.ts` |
| Files (registers find too) | `FileCommands.cs` | `files.py` | `files.ts` |
| find | `Find.cs` | `find.py` | `find.ts` |
| Text (registers sed and diff too) | `TextCommands.cs` | `text.py` | `text.ts` |
| sed | `Sed.cs` | `sed.py` | `sed.ts` |
| diff | `Diff.cs` | `diff.py` | `diff.ts` |
| awk | `Awk.cs` | `awk.py` | `awk.ts` |
| jq | `Jq.cs` | `jq.py` | `jq.ts` (+ `jq-parse.ts`, `jq-ops.ts`, `jq-values.ts`; §7) |
| bc | `Bc.cs` | - | - |
| Binary and checksums | `BinaryCommands.cs` | `binary.py` | `binary.ts` |
| Archives | `ArchiveCommands.cs` | `archive.py` | `archive.ts` (+ `zipfile.ts`, `../compress.ts`, `../inflate.ts`) |
| Documents (`to-markdown`) | `DocumentCommands.cs` | `documents.py` | `documents.ts` (`commands/xml.ts` goes with `convert.ts` to `xml.ts`) |
| curl, wget | `WebCommands.cs` | `web.py` (now `net.py`) | `web.ts` |
| git: options, repository | `Git.cs` | `git.py` | `git.ts` |
| git: revisions, dispatcher, diff engine | `GitRevisions.cs` | `gitrevisions.py` | `gitrevisions.ts` |
| git: init, status, add, rm, mv, commit, log, show, diff | `GitCommands.cs` | `gitcommands.py` | `gitcommands.ts` |
| git: checkout … config | `GitMore.cs` | `gitmore.py` | `gitmore.ts` |
| git: apply, am, format-patch, bisect | `GitPatch.cs` | `gitpatch.py` | `gitpatch.ts` |
| git: object formats | `GitCore.cs` | `gitcore.py` | `gitcore.ts` |
| git: pack files | `GitPack.cs` | `gitpack.py` | `gitpack.ts` |

### Modules only one or two ports have

| Module | Port | Why |
|---|---|---|
| `HostCommand.cs` / `host.py` | C#, Python | Host commands (bubblewrap) aren't offered in TS |
| `AttributeBinder.cs` / `binder.py` | C#, Python | Attribute or decorator binding; TS registers plain functions |
| `OffContext.cs` | C# | |
| `containers.py`, `mcp.py`, `web.py` (the web terminal) | Python | |
| `regex.ts`, `md5.ts`, `sha1.ts`, `compress.ts`, `inflate.ts` | TS | The platform has no POSIX regex, hashes or zlib built in |
| `integrations/*` | Python, TS | Per-framework adapters; each list has its own frameworks |
| `Bc.cs` | C# | Not ported yet |

## 3. The shell split

### 3.1 Sizes

Approximate line counts after the split (C#: measured):

| Part | C# | Python | TS |
|---|---|---|---|
| Shell (core) | 500 | 430 | 450 |
| Types | 175 | 250 | 150 |
| Execution (+ `CondEvaluator`) | 590 | 540 | 500 |
| Redirection | 190 | 220 | 180 |
| Invocation | 390 | 390 | 370 |
| Variables (incl. arrays) | 420 | 470 | 380 |
| Expansion | 545 | 650 | 500 |
| Session | 120 | 120 | 70 |
| Glob (brace + glob) | 320 | 370 | 300 |

### 3.2 Section → symbol mapping

Each cell lists the current symbols that go to that file. Module-level constants and regexes go with their only
user. A constant used by several parts goes to Types. The section names below are the `---------- name ----------`
markers, the same in all three ports and in this order; `layout.json` repeats them and the checker compares (§8).
A port with nothing for a section keeps the marker and one comment line saying so. Types and Session have no
markers. In Python and TS, the module-level functions and helper classes of a part follow the part class under a
last marker `helpers` (C# has none: its static helpers sit in their section).

The C# column is final: phase 1 placed every member of `Shell` and the compiler, the line check and an all-member
reflection dump confirmed it. It lists every member in file order. Members the first draft missed: `BecomeNewProcess`,
`CheckIterations`, `DefineFunction`, `IsJob` (Execution); `ReadRedirectAsync`, `Descriptor` (Redirection);
`ReportNotFound`, `BadNumber`, `PathCandidates`, `ScriptInterpreter`, `InFunction` (Invocation); `OptionFlags`,
`CheckWritable`, `Converted`, `BashVersInfo`, `ShellVariable`, `LookUp`, `Ifs`, `IfsJoiner`, `IsSet`, `ScratchVariable`
(Variables); `BadSubstitutionStop`, `Fatal`, `ParamRe`/`IndirectRe`/`PrefixRe`, `IndirectTarget`, `ParamWord`,
`ParamWordAsync`, `CheckSet`, `AssignmentTilde`, `TildeValue`, `TildePrefix` (Expansion); `SyncProgramFiles`
(registration); `RegexTimeout`, `Timed` (limits); `NextEpoch` (state). Where C# and Python orders differed, C# took
Python's: `SyncHashed` after `HashLookup`, `TraceQuote`/`QuoteValue` at the end of `functions`, `ArrayRef`/`Unquote` at
the end of `arrays`, `InputFd` before `WithFd`.

**All instance state stays in `Shell/Shell.cs`, not only the fields at the top.** Today's `Shell.cs` declares about a
dozen fields next to their first user (`Recorder`, `_variableChars`, `_inErrTrap`/`_inDebugTrap`, `LoopDepth`,
`_totalIterations`, `_scriptDepth`, `_callDeadline`, `_unexported`, `_arithDepth`, `_paramWords`, and `Shopts`/`Traps`
after `NextRandom`). They all move to the state section. Static constants (`RegexTimeout`, `BadNumber`, `BashVersInfo`,
`ScratchVariable`, `ParamRe`…, `AssignmentTilde`, `SnapshotSetOptions`) go with their user: none reads another static.
Python and TS need no such move (their state is assigned in `__init__` / declared in the class).

**Shell (core): `Shell/Shell.cs` · `shell/__init__.py` · `shell.ts`.** The class declaration and all state stay
here in every port: TS fields and accessors can't move (§3.3), and C# field initialisers in different files of a
partial class run in no defined order. That includes the C# `Web` property, now in `WebAccess.cs`. `AllowWeb` /
`allow_web` / `allowWeb` is host configuration like `registerApi`, so it goes in the registration section (where
TS has it today), not in Session.

| Section | C# | Python | TS |
|---|---|---|---|
| state and constructor | the fields `_commands`…`_condDepth` (with `s_epochs`, `NextEpoch`), `Shell(...)` ×2, `SeedSystemFiles`, `SystemDirs`, `OsRelease`…`DotBashrc`, `InstalledAt`, `_builtinNames`, `Oct`, `_procSubCount`, the properties `Options`…`_random` (with `ErrPrefix`), `Shopts`, `Traps`; then every other instance field, from where it sat next to its first user: `Recorder`, `Web` (from `WebAccess.cs`), `_variableChars`, `_inErrTrap`/`_inDebugTrap`, `LoopDepth`, `_totalIterations`, `_scriptDepth`, `_callDeadline`, `_unexported`, `_arithDepth`, `_paramWords`; `Cwd`, `ResolvePath` | `_DEFAULT_SHOPTS`, `SYSTEM_DIRS`, `OS_RELEASE`…`DOT_BASHRC`, `Shell.__init__`, `installed_at`, `_seed_system_files`, `cwd`, `resolve`, `err_prefix` | `SYSTEM_DIRS`, `OS_RELEASE`…`DOT_BASHRC`, fields, `constructor`, `seedSystemFiles`, `cwd`, `resolvePath`, `errPrefix`, plus every accessor: `ifs`, `ifsJoiner`, `optionList`, `optionFlags`, `virtualNow`, `randomState` |
| clock and random | `Now`, `NowUtc`, `SleepAsync`, `WithVirtualTimeoutAsync`, `VirtualTimeout`, `StartTimer`, `NextRandom` | - (Python has no virtual clock yet) | `now`, `sleepVirtual`, `withVirtualTimeout`, `nextRandom` |
| registration | `Register`, `WriteProgramFile`, `SyncProgramFiles`, `Register`, `Register`, `RegisterReplacing`, `AllowWeb`, `AllowWeb`, `Unregister` (`AllowWeb` ×2 from `WebAccess.cs`) | `register_command`, `_write_program_file`, `register`, `_register_replacing`, `allow_web`, `unregister` | `register`, `writeProgramFile`, `registerTools`, `mountMcp`, `registerApi`, `allowWeb`, `unregister` |
| run | `RunAsync`, `RunRecordedAsync`, `RunCallAsync` | `run`, `run_async`, `_run_call` | `run` |
| limits | `Size`, `TooLarge`, `TooLarge`, `ChargeVariable`, `Stopped`, `Seconds`, `RegexTimeout`, `Timed` | `size_text`, `_seconds`, `_stopped`, `too_large`, `charge_variable`, `check_deadline` | `chargeVariable`, `checkTime`, `remainingMs`, `stopped`, `seconds` |
| re-exports | the marker and `// nothing: C# has no module paths to keep working` | every name imported from `topwater.shell` today: `CommandContext`, `RegisteredCommand`, `Shell`, `ShellResult`, `ShellArray`, `StdinBuffer`, `CallTimeout`, `LimitExceeded`, `ExitSignal`, `ReturnSignal`, `LoopControl`, `_NewProcess`, `size_text`, `quote_value`, `split_fields`, `SNAPSHOT_SET_OPTIONS`, `glob_to_regex`, `TimedPattern`, and every other module-level name `shell.py` has today (`Handler`, `ShellError`, …: a host may import any of them) | `export * from './shell/types.js'`, plus `splitFields`, `arrayRef`, `BufferWriter` |

**Types: `Shell/Types.cs` · `shell/types.py` · `shell/types.ts`**

| C# | Python | TS |
|---|---|---|
| the top of today's `Shell.cs` unchanged: `ShellOptions`, `ShellResult`, `CommandHandler`, `RegisteredCommand`, `StdinBuffer`, `CommandContext`, `ShellArray`, `ControlFlow`, `LoopControl`, `ReturnSignal`, `ExitSignal`, `LineAbort`, `FatalError`, `LimitExceeded`; then a `partial class Shell` block: `Io`, `ExecutableStub`, `NewProcess` | `EXECUTABLE_STUB`, `ShellResult`, `RegisteredCommand`, `_Null`, `StdinBuffer`, `_Io`, `_BadSubstitution`, `_ControlFlow`, `_LineAbort`, `LoopControl`, `ReturnSignal`, `ExitSignal`, `LimitExceeded`, `CallTimeout`, `CommandContext`, `ShellArray`, `_NewProcess` | `ShellOptions`, `DEFAULTS`, `ShellResult`, `Io`, `ShellArray`, `BadSubstitution`, `ControlFlow`, `LineAbort`, `LoopControl`, `ReturnSignal`, `ExitSignal`, `FunctionDef`, `NewProcess`, `EXECUTABLE_STUB`, each exported (`SavedState` goes to `execution.ts`, its only user, like C# `State`; TS keeps `StdinBuffer`, `CommandContext` and `LimitExceeded` in `command.ts`) |

**Execution: `Shell/Execution.cs` · `shell/execution.py` · `shell/execution.ts`.** Sections: `scripts` (run script
text … restore state), `lists` (top level, script, list pipeline), `jobs` (background, wait, forget), `pipelines`,
`compound commands` (exec command … case), `conditional expressions` (`CondEvaluator`).

| C# | Python | TS |
|---|---|---|
| scripts: `RunScriptTextAsync`, `SourceLoginFilesAsync`, `BecomeNewProcess`, `RunExitTrapAsync`, `State`, `SaveState`, `RestoreState`; lists: `ExecTopLevelAsync`, `ExecScriptAsync`, `ExecListPipelineAsync`; jobs: `ExecBackgroundAsync`, `WaitFor`, `WaitNext`, `IsJob`, `ForgetJobs`; pipelines: `ExecPipelineAsync`, `RunPipelineAsync`; compound commands: `ExecCommandAsync`, `DefineFunction`, `ExecSubshellAsync`, `ConditionAsync`, `ExecIfAsync`, `LoopBodyAsync`, `CheckIterations`, `ExecLoopAsync`, `ExecForAsync`, `ExecSelectAsync`, `ExecArithForAsync`, `ExecCaseAsync`; then, after the class, the `conditional expressions` marker and class `CondEvaluator` | `_run_script_text`, `_source_login_files`, `_become_new_process`, `_run_exit_trap`, `_save_state`, `_restore_state`, `_exec_script`, `_exec_list_pipeline`, `_exec_background`, `wait_for`, `wait_next`, `forget_jobs`, `_exec_pipeline`, `_run_pipeline`, `_exec_command`, `_exec_subshell`, `_condition`, `_exec_if`, `_loop_body`, `_check_iterations`, `_exec_loop`, `_exec_for`, `_exec_select`, `_exec_arith_for`, `_exec_case`; class `_CondEvaluator` | `runScriptText`, `sourceLoginFiles`, `becomeNewProcess`, `runExitTrap`, `saveState`, `restoreState`, `execTopLevel`, `execScript`, `execListPipeline`, `execBackground`, `waitFor`, `waitNext`, `execPipeline`, `runPipeline`, `execCommand`, `execSubshell`, `condition`, `execIf`, `loopBody`, `loopControl`, `checkIterations`, `execLoop`, `execFor`, `execSelect`, `execArithFor`, `execCase`; class `CondEvaluator` |

**Redirection: `Shell/Redirection.cs` · `shell/redirection.py` · `shell/redirection.ts`.** Sections: `writers`,
`redirections`.

| C# | Python | TS |
|---|---|---|
| writers: `CappedWriter`, `FileSink`, `FileAppender`, `SwitchWriter`; redirections: `ApplyRedirectsAsync`, `OpenInputAsync`, `ReadRedirectAsync`, `OpenOutputAsync`, `Descriptor`, `ExecRedirectsAsync`, `InputFd`, `WithFd` | `CappedWriter`, `FileSink`, `FileAppender`, `SwitchWriter`, `_apply_redirects`, `_open_input`, `_open_output`, `_descriptor`, `_exec_redirects`, `input_fd`, `_with_fd` | `BufferWriter`, `FileSink`, `FileAppender`, `SwitchWriter`, `applyRedirects`, `openInput`, `openOutput`, `descriptor`, `execRedirects`, `inputFd`, `withFd` |

**Invocation: `Shell/Invocation.cs` · `shell/invocation.py` · `shell/invocation.ts`.** Sections: `simple commands`
(exec simple, expand declaration, report not found), `invoke` (invoke, make context), `script files` (run script
file, run interpreter), `lookup` (is unfiled … is app only), `functions` (call function, in function, is builtin).
C# has `lookup` before `script files` and `SyncHashed` last today: it reorders to the Python order.

| C# | Python | TS |
|---|---|---|
| simple commands: `IsDeclaration`, `ExecSimpleAsync`, `ExpandDeclarationAsync`, `ReportNotFound`; invoke: `InvokeAsync`, `BadNumber`; script files: `RunScriptFileAsync`, `ScriptInterpreter`, `RunInterpreterAsync`; lookup: `IsUnfiled`, `StubCommand`, `PathCandidates`, `SearchPath`, `SearchPathAll`, `HashLookup`, `SyncHashed`, `Locate`, `LocateAll`, `IsAppOnly`; functions: `CallFunctionAsync`, `InFunction`, `IsBuiltin`, `TraceQuote`, `QuoteValue` | `_exec_simple`, `_expand_declaration`, `_report_not_found`, `_invoke`, `_run_script_file`, `_interpreter_chain_too_long`, `_run_interpreter`, `is_unfiled`, `stub_command`, `_path_candidates`, `search_path`, `search_path_all`, `hash_lookup`, `sync_hashed`, `locate`, `locate_all`, `_is_app_only`, `_call_function`, `in_function`, `is_builtin`, `trace_quote`, `quote_value` | `execSimple`, `expandDeclaration`, `reportNotFound`, `invoke`, `makeContext`, `runScriptFile`, `runInterpreter`, `isUnfiled`, `stubCommand`, `pathCandidates`, `searchPath`, `searchPathAll`, `hashLookup`, `syncHashed`, `locate`, `locateAll`, `isAppOnly`, `callFunction`, `isBuiltin`, `traceQuote`, `standardOption` |

**Variables: `Shell/Variables.cs` · `shell/variables.py` · `shell/variables.ts`.** Sections: options, scalars, arrays.
`GetVariable` and its helpers move here from the top of today's expansion section, because they read variables.

| Section | C# | Python | TS |
|---|---|---|---|
| options | `IsVariableSet`, `OptionList`, `SetOption`, `DeclareLocal`, `OptionFlags` | `is_variable_set`, `option_list`, `set_option`, `declare_local`, `_option_flags` | `isVariableSet`, `setOption`, `declareLocal` |
| scalars | `HasAttr`, `IsExported`, `MarkShellVariable`, `Unexport`, `SetAttrs`, `Resolve`, `SetVar`, `Converted`, `CheckWritable`, `BashVersInfo`, `GetArray`, `ShellVariable`, `GetVariable`, `LookUp`, `Ifs`, `IfsJoiner`, `IsSet` | `has_attr`, `is_exported`, `mark_shell_variable`, `unexport`, `set_attrs`, `resolve_name`, `set_var`, `_converted`, `_check_writable`, `get_array`, `_shell_variable`, `get_variable`, `_look_up`, `ifs`, `_ifs_joiner`, `_is_set`, `_BASH_VERSINFO`, `_SCRATCH` | `hasAttr`, `isExported`, `markShellVariable`, `unexport`, `setAttrs`, `resolve`, `setVar`, `converted`, `checkWritable`, `getArray`, `shellVariable`, `getVariable`, `lookUp`, `isSet`, `BASH_VERSINFO`, `SCRATCH` |
| arrays | `Writable`, `DeclareArray`, `ArrayKey`, `GetElement`, `SetElement`, `Unset`, `ArrayElementsAsync`, `ScratchVariable`, `ElementOpAsync`, `ExpandSubscriptAsync`, `SubscriptAsync`, `AssignArrayAsync`, `DeclareArraysAsync`, `ArrayRef`, `Unquote` | `writable`, `declare_array`, `array_key`, `get_element`, `set_element`, `unset`, `_array_elements`, `_element_op`, `expand_subscript`, `_subscript`, `_assign_array`, `_declare_arrays`, `array_ref`, `_unquote` | `writable`, `declareArray`, `arrayKey`, `getElement`, `setElement`, `unset`, `arrayElements`, `elementOp`, `expandSubscript`, `subscript`, `assignArray`, `declareArrays`, `arrayRef`, `unquote`, `splitOnce`, `splitSubscript` |

**Expansion: `Shell/Expansion.cs` · `shell/expansion.py` · `shell/expansion.ts`.** Sections: `parts and arithmetic`
(expand part, arith), `parameters` (bad substitution … check set), `substitution` (run, process, capture, read
file word), `words` (expand single, assignment, pattern, tilde, alternative word, expand words, field splitting).

| C# | Python | TS |
|---|---|---|
| parts and arithmetic: `ExpandPartAsync`, `EvalArithAsync`, `ArithExpansionAsync`; parameters: `BadSubstitution`, `BadSubstitutionStop`, `ExpandPartInAsync`, `Fatal`, `ParamRe`, `IndirectRe`, `PrefixRe`, `DefaultOp`, `IndirectTarget`, `ParamWord`, `ParamWordAsync`, `ReplacementAsync`, `ExpandParameterAsync`, `CheckSet`; substitution: `RunSubstitutionAsync`, `ProcessSubstitutionAsync`, `CaptureAsync`, `ReadFileWord`; words: `ExpandSingleAsync`, `AssignmentTilde`, `ExpandAssignmentAsync`, `PatternAsync`, `TildeValue`, `TildePrefix`, `SplitFields`, `Field`, `AlternativeWordAsync`, `ExpandWordsAsync` | `_expand_part`, `_arith`, `_arith_expansion`, `_bad_substitution_text`, `_fatal`, `_indirect_target`, `_param_word`, `_param_word_expand`, `_replacement`, `_expand_parameter`, `_check_set`, `_run_substitution`, `_process_substitution`, `_capture`, `_expand_single`, `_expand_assignment`, `_pattern`, `_tilde_value`, `_tilde_prefix`, `_alternative_word`, `_expand_words`; module: `_PARAM`, `_INDIRECT`, `_PREFIX`, `_ASSIGNMENT_TILDE`, `default_op`, `_arith_nesting`, `split_fields`, `_read_file_word`, `_Field` | `expandPart`, `evalArithExpr`, `arithExpansion`, `fatal`, `indirectTarget`, `paramWord`, `paramWordValue`, `replacement`, `expandParameter`, `checkSet`, `runSubstitution`, `processSubstitution`, `capture`, `expandSingle`, `expandAssignment`, `pattern`, `tildeValue`, `tildePrefix`, `alternativeWord`, `expandWords`; module: `PARAM_RE`, `INDIRECT_RE`, `PREFIX_RE`, `defaultOp`, `splitFields`, `readFileWord` |

**Session: `Shell/Session.cs` · `shell/session.py` · `shell/session.ts`.** No markers. Order: snapshot, restore,
fork, save, load, start recording, capture state, apply state. The format files (`ShellSnapshot.cs`, `Recording.cs`,
`WebAccess.cs`) then hold no `partial class Shell` block: everything `Shell` can do is under `Shell/`, as it already
is in Python and TS.

| C# | Python | TS |
|---|---|---|
| the `partial class Shell` blocks from the end of `ShellSnapshot.cs` and `Recording.cs`, in this order: `Snapshot`, `Restore`, `Fork`, `Save`, `Load`, `StartRecording`, `AppCommandNames`, `CaptureState`, `SnapshotSetOptions`, `ApplyState` | `SNAPSHOT_SET_OPTIONS`, `snapshot`, `restore`, `fork`, `save`, `load`, `start_recording`, `_capture_state`, `_apply_state` | `captureState`, `applyState` (TS snapshots through `ShellSnapshot.capture` and records through `startRecording(shell)`; `SNAPSHOT_SET_OPTIONS` stays in `snapshot.ts`) |

**Glob: `Glob.cs` · `glob.py` · `glob.ts`**

| C# | Python | TS |
|---|---|---|
| the `brace expansion` marker and class `Brace`, then the `globbing` marker and class `Glob` (both from the end of `Shell.cs`, unchanged) | brace section (`_MAX_BRACE`, `_PLACEHOLDER`, `brace_expand`, `_rebuild`, `_brace_text`, `_brace_close`, `_brace_alternatives`), then globbing section (`has_wildcard`, `glob_escape`, `_has_unescaped_wildcard`, `_RUNNING`, `TimedPattern`, `_cached`, `glob_to_regex_body`, `_glob_body`, `_group_end`, `_bracket_end`, `_bracket`, `glob_to_regex`, `_join`, `glob_expand`) | add the brace section from `shell.ts` (`MAX_BRACE_ITEMS`, `PLACEHOLDER`, `braceExpand`, `expandBraceText`, `findClose`, `alternatives`) above the existing glob code |

### 3.3 How the parts reach each other

| | C# | Python | TypeScript |
|---|---|---|---|
| Mechanism | `public sealed partial class Shell` in every file (the class is already partial), all in `namespace Topwater;` (a namespace `Topwater.Shell` would collide with the class) | Stateless mixins: `class ExecutionPart:` and so on, with no `__init__`, no class attributes and no base class; `class Shell(SessionPart, ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart)` in `__init__.py` | Each part is a class that is never instantiated, `export class ExecutionPart { async execScript(this: Shell, …): Promise<number> {…} … }`, mixed into `Shell` in `shell.ts` (below) |
| Method bodies | Unchanged | Unchanged (`self.` calls resolve through the MRO) | Unchanged, and no commas to add between methods (`this.` calls resolve through the prototype); each signature gains `this: Shell` |
| Private state | Private fields stay private; partials share them | Unchanged | `private` members a part touches become `/** @internal */`, the convention `shell.ts` already uses 84 times |
| Types and helpers | Nested types stay nested in the part that uses them. A static field whose initialiser reads another static field stays in the same file, below it | Parts import from `.types`, `..glob`, `..parser`, `..vfs`, never from `topwater.shell` itself. `Shell` is needed at runtime only in `load`/`fork`: import it inside the function (not `type(self)`: that would change what a subclass forks into) | Parts use `import type { Shell } from '../shell.js'` and runtime imports from `./types.js`. No part imports runtime values from `shell.ts`. A type named in a moved method's signature must be exported from its new module (`declaration: true` requires it) |

TypeScript mixing in `shell.ts`: the handbook's mixin pattern (an interface merged with the class, methods copied
from each part's prototype). Copy property descriptors, so the methods stay non-enumerable as class methods are.

```ts
export interface Shell extends ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart, SessionPart {}
export class Shell implements ArithHost { /* fields, constructor, accessors, core methods */ }
for (const part of [ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart, SessionPart])
  for (const k of Object.getOwnPropertyNames(part.prototype))
    if (k !== 'constructor') Object.defineProperty(Shell.prototype, k, Object.getOwnPropertyDescriptor(part.prototype, k)!);
```

Why classes and not exported method objects (`const ExecutionPart = { … }`): a method moves without a comma added
after it; the declaration output keeps methods as methods, so a host's subclass can still override one; and
`stripInternal` can remove an `@internal` member of a class from the `.d.ts` but not a member of an object literal
(tried with TypeScript 7.0.2). That keeps open the option of publishing types with only the public members.
`stripInternal` itself is not part of this work: it would also remove the 111 members marked `@internal` today.

TypeScript constraints:
- Accessors (`get`/`set`) can't take a `this` parameter, so all of them stay in the class. `private get optionFlags`
  and `ifsJoiner` lose `private` like the fields the parts read.
- A method that returns `this` (`register`, `allowWeb`, …) stays in the class.
- Add an explicit return type only where `tsc` asks for one (TS7023/TS7024, inference looping through `Shell`).
- What package users see: `dist/shell.d.ts` gains typed `@internal` members where it has `private name;` lines
  today, and `dist/shell/*.d.ts` appear. The public members must not change: compare before and after (§9).

C# constraints (what the pilot found):
- Each file has the usings it needs and no others; `dotnet build -p:EnforceCodeStyleInBuild=true` with IDE0005 raised to
  a warning (a temporary `.editorconfig`) lists the unused ones. `ImplicitUsings` covers the rest.
- The class's `<summary>` stays on the declaration in `Shell/Shell.cs`; the other parts have none.
- A part's top-level types follow the class, under their own marker (`CondEvaluator` under `conditional expressions`).
- Nothing else is needed: no access modifier changes, and the public and internal surface is identical (§9).

Python constraints:
- A module the parts import (`glob.py`) must not import `topwater.shell` or `topwater.shell.types` at module level:
  importing a submodule runs `shell/__init__.py` first, which imports the parts, which import the half-loaded
  `glob`. `TimedPattern._run` imports `CallTimeout` inside the function. Same for any later root module.
- No two parts define the same name (the first in the MRO would win silently). §9 has the one-line check.
- Cost: none that matters. Method lookup through six bases is cached per type by CPython; `fork` and `load` build a
  `Shell` exactly as now; nothing pickles a shell (snapshots have their own format). `help(Shell)` groups methods
  by part, which is fine.

## 4. Conventions

### 4.1 Order of sections in a file

1. A one-line header comment: what the file holds, and "see docs/architecture.md".
2. Imports or usings.
3. Constants and regexes that only this file uses.
4. Types local to the file (C# nested types, Python/TS helper classes).
5. Sections marked `// ---------- name ----------` (`#` in Python), with the same names and order in all three
   ports, as listed in §3.2 and `layout.json`. Command domain files need no markers.
6. Inside a section: the entry point first, then its helpers, in the order of the Python column.
7. Command domain files: helpers first, then the commands in the order of the §5 table (the row's `commands` list
   in `layout.json`; the checker compares the order of the registration calls). C# and TS register most commands
   as lambdas inside the registrar; they stay lambdas, cut and pasted into `RegisterXxxCommands` / `registerXxx`
   of the new file. Turning them into named functions is not a move. Python keeps named functions, defined in the
   same order, and its `register` at the end of the file.

### 4.2 Names

| Thing | C# | Python | TS |
|---|---|---|---|
| Shell part file | `Shell/Execution.cs` | `shell/execution.py` | `shell/execution.ts` |
| Part container | `partial class Shell` | `class ExecutionPart` (mixin) | `class ExecutionPart` (class part, §3.3) |
| Command domain file | `Builtins/XxxCommands.cs` | `commands/xxx.py` | `commands/xxx.ts` |
| Single-program file | `Builtins/Sed.cs` | `commands/sed.py` | `commands/sed.ts` |
| Method | `ExecSimpleAsync` | `_exec_simple` (`_` = private) | `execSimple` |
| Registrar | `RegisterProcessCommands` | `register` | `registerProcess` |

The root of a name is the same in every port: `ExecSimpleAsync` = `_exec_simple` = `execSimple`.

### 4.3 Name differences to fix later

These get their own rename-only commits after the moves:

| C# | Python | TS | Proposal |
|---|---|---|---|
| `Resolve` (nameref) | `resolve_name` | `resolve` | `ResolveName` / `resolve_name` / `resolveName` |
| `ResolvePath` | `resolve` | `resolvePath` | public in Python: leave it |
| `EvalArithAsync` | `_arith` | `evalArithExpr` | `_eval_arith`, `evalArith`… |
| `ExecTopLevelAsync` | `_exec_script(top=True)` | `execTopLevel` | add `_exec_top_level` in Python |
| `Register` | `register_command` | `register` | public API: leave it |

## 5. Command grab-bags by domain

`MoreCommands.cs`, `more.py` and `builtins.ts` go away. So do the shell and system blocks in `Builtins.cs` and
`commands/__init__.py`. The shared helpers stay in `Builtins.cs` / `commands/__init__.py` / `command.ts`.

The order of each list is the order of registration in the new file, the same in all three ports. It keeps the
order Python has today (where C# already agrees), so most blocks move without being reordered. `tools/layout.json`
has the same lists, and the checker compares them with the registration calls (§8).

| Domain file | Commands, in registration order |
|---|---|
| Scripting | `true` `:` `false` `echo` `printf` `history` `which` `type` `test` `[` `bash` `sh` `source` `.` `help` `exit` `return` `break` `continue` `shift` `trap` `shopt` `set` `getopts` `exec` `hash` `builtin` `eval` `let` `command` |
| Variables | `export` `unset` `env` `alias` `unalias` `local` `declare` `typeset` `readonly` `read` `mapfile` `readarray` `printenv` |
| Process | `ps` `wait` `jobs` `disown` `kill` `ulimit` `times` `umask` `sleep` `timeout` `time` |
| System | `whoami` `hostname` `uname` `date` `clear` `sudo` `apt` `apt-get` `apt-cache` `dpkg` `su` `id` `uptime` `free` `nproc` |
| Paths | `cd` `pwd` `pushd` `popd` `dirs` `basename` `dirname` `realpath` `readlink` `mktemp` |
| Files | `ls` `cat` `mkdir` `touch` `rm` `rmdir` `cp` `mv` `ln` `find` `tree`, then gains `stat` `du` `df` `chmod` `chown` `chgrp` `less` `more` `file` |
| Text | `grep` `egrep` `fgrep` `head` `tail` `wc` `sort` `uniq` `cut` `tr` `tee` `xargs` `sed`, then gains `nl` `rev` `tac` `paste` `comm` `join` `column` `seq` `diff` |
| Binary | `cmp` `xxd` `hexdump` `od` `strings`, then gains `base64` `md5sum` `sha1sum` `sha256sum` `sha512sum` |

`find`, `sed` and `diff` are registered by Files and Text, as C# and Python do today; their engines live in
`Find`, `Sed` and `Diff` (TS moves its `register('sed', …)` from `sed.ts` into `registerText`). Every other
single-program file (`awk`, `jq`, `bc`, `to-markdown`, `git`) registers its own command.

Where each block comes from:

| To | C# from | Python from | TS from |
|---|---|---|---|
| Scripting | `Builtins.cs`: `IsShellBuiltin`; from `RegisterShellCommands` true, :, false, echo, printf, history, which, type, test, [, the `runScript` handler, bash, sh, source, ., help; class `Test` (after the class). `MoreCommands.cs`: `ParseCount`, `LoopJump`, `ShellKeywords`, `Describe`; exit, return, break, continue, shift, trap, shopt, set, getopts, exec, hash, builtin, eval, let, command | `__init__.py`: `_echo`, `_printf`, `_which`, `_type`, `_help`, history, `test_unary`, `newer`, `_test_binary`, `_is_int`, `evaluate_test`, `_bracket`, `_run_script`; `more.py`: `_count`, `_exit`, `_return`, `_break`, `_shift`, `_set`, `_trap_name`, `_trap`, `_shopt`, `describe`, `_getopts`, `_exec`, `_hash`, `_builtin`, `_eval`, `_let`, `_command` | `builtins.ts`: `testUnary`, `testNewer`, `BINARY_OPS`, `testBinary`, `testEvaluate`, `SHELL_KEYWORDS`, `parseCount`, `loopJump`, `describe` and those commands |
| Variables | `Builtins.cs`: export, unset, env, alias, unalias. `MoreCommands.cs`: `EndsWithEscape`, `ReadFields`, `PrintFunctions`, `AttributeFlags`, `DeclareLine`; the `declare` handler, local, declare, typeset, readonly, read, the `mapfile` handler, mapfile, readarray, printenv | `__init__.py`: `_flag_args`, `_export`, `_env`, `_unset`, `_alias`, `_unalias`; `more.py`: `_print_functions`, `attribute_flags`, `_declare_quote`, `declare_line`, `_declare`, `_ends_with_escape`, `read_fields`, `_input_fd`, `_read`, `_mapfile`, `_printenv` | `builtins.ts`: `readFields`, `attributeFlags`, `declareLine`, `allVariableNames`, `inputFor` and those commands |
| Process | `Builtins.cs`: ps. `MoreCommands.cs`: `ParseDuration`, `OctalMask`, `SymbolicMask`; wait, jobs, disown, kill, ulimit, times, umask, sleep, timeout, time | `__init__.py`: ps; `more.py`: `_duration`, `_wait`, `_kill`, `_ulimit`, `_umask_symbolic`, `_umask_parse`, `_umask`, `_sleep`, `_timeout`, `_time` | `builtins.ts`: `symbolicMode`, `parseDuration`, `sleepFor` and those commands |
| System | `Builtins.cs`: `IsoUnits`, `IsoFormats`, `DateUnits`, `MonthAbbr`, `DayAbbr`, `ZoneHours`, `ParseDate`, `ParseDateOrThrow`; from `RegisterSystemCommands` whoami, hostname, uname, date (the registrar keeps its name). `MoreCommands.cs`: clear, sudo, the apt/apt-get/apt-cache loop, dpkg, su, id, uptime, free, nproc | `__init__.py`: `_uname`, `parse_date`, `_parse_date`, `_date`, `_register_system_commands`; `more.py`: `_sudo`, `_package_manager`, `_su`, `_id`, `_uptime`, `_free` | `builtins.ts`: `ISO_*`, `MONTH_ABBR`, `DAY_ABBR`, `ZONE_HOURS`, `DATE_UNITS`, `parseDate` and those commands |
| Paths | `Builtins.cs`: cd, pwd. `MoreCommands.cs`: the local functions `Dirs` and `ChangeDir`, pushd, popd, dirs, basename, dirname, the `realpath` handler, realpath, readlink, mktemp | `__init__.py`: `_cd`, `_pwd`; `more.py`: `_dirs_line`, `_change_dir`, `_pushd`, `_popd`, `_dirs`, `_basename`, `_dirname`, `_realpath`, `_mktemp` | `builtins.ts`: cd, pwd, pushd, popd, dirs; `files.ts`: basename, dirname, realpath, readlink, mktemp |
| Files | `MoreCommands.cs`: `LooksJson` (before the registrar); stat, du, df, chmod, the `chown` handler, chown, chgrp, the `pager` handler, less, more, file (at the end of `RegisterFileCommands`); class `Chmod` (at the end of the file) | `more.py`: `_stat`, `_du`, `_df`, `chmod_parse`, `_chmod`, `_chown`, `_pager`, `_looks_json`, `_file` | already there |
| Text | `MoreCommands.cs`: `TwoInputs`, `CommCmd`, `JoinCmd` (before the registrar); nl, rev, tac, paste, comm, join, column, seq, diff (at the end of `RegisterTextCommands`) | `more.py`: `_nl`, `_rev`, `_tac`, `_paste`, `_two_inputs`, `_comm_cmd`, `_join_cmd`, `_column`, `_seq`, and the registration of `_diff` | already there |
| Binary | `MoreCommands.cs`: `Hex` (before the registrar); base64 and the checksum loop (at the end of `RegisterBinaryCommands`) | `more.py`: `_base64`, `_hasher` | already there |
| Diff | class `Diff` from `MoreCommands.cs`, in phase 5 (the file goes away then) | `_diff`, `_join`, `_diff_dirs`, `_diff_header`, `_diff_files` from `more.py` | the diff section of `text.ts` |
| Sed | class `Sed` from `TextCommands.cs` | the sed section of `text.py` (`_SedCmd` … `_sed`) | already `sed.ts` |
| Find | already `Find.cs` | `_Find` … `_find` from `files.py` | the find section of `files.ts` |
| Format (root) | class `Printf`, and a `partial class Builtins` block with `Unescape`, `ByteChar`, `DayNames`, `MonthNames`, `Strftime` (all from `Builtins.cs`) | `unescape`, `_byte_char`, `_c_integer`, `_c_float`, `shell_quote`, `printf_format`, `strftime` from `commands/__init__.py` (re-exported there) | already `format.ts` |
| Web | already `WebCommands.cs` | rename `net.py` → `web.py` | already `web.ts` |

The registrars are called in one order in all three ports (today each port has its own): scripting, variables,
process, system, paths, files, text, awk, jq, bc, binary, archive, documents, web, git; the order of the rows with
`commands` in `layout.json`. No command is registered twice in any port, so the order changes no winner; only the
order in which a host enumerating `Shell.Commands` sees them. §9 has the check. C# names the registrars
`RegisterScriptingCommands`, `RegisterVariableCommands`, `RegisterProcessCommands`, `RegisterSystemCommands`,
`RegisterPathCommands`; `RegisterShellCommands` and `RegisterUtilityCommands` go.

Registrars in C# and TS are cut at statement level: each `Register` call with its leading comments moves whole. A
local the registrar declares for several commands (`CommandHandler declare`, `runScript`, `realpath`, `chown`, `pager`,
`mapfile`, the `Dirs`/`ChangeDir` local functions) moves with them; in every port each such local serves commands of
one domain only. A helper that several domains use stays in the shared helper file: `ExecFailure` / `exec_failure`
(env, xargs, timeout) and the quoting, input and message helpers.

Small helpers used by several domains move to the shared helper file: TS `ioOf` and `line` go to `command.ts`.
Engine extractions (Sed, Find, Diff) leave the registration line where it is now.

## 6. git: Python and TS follow C#

Python `git.py` and TS `git.ts` already have the same `---------- … ----------` sections in the same order, so the
split is five cuts along existing markers:

| Target (C# already has it) | Sections (Python lines today / TS lines today) |
|---|---|
| `Git.cs` → `git.py`, `git.ts` | option parsing, the repository (py 1–621 / ts 1–540) |
| `GitRevisions.cs` → `gitrevisions.py`, `gitrevisions.ts` | revisions, the command (`Git` context, `valid_head`, `clean_ident`, the `_git`/`git` dispatcher, `register`), shared pieces (diff engine, `Status`, `write_diff`…) (py 622–1503 / ts 541–1323) |
| `GitCommands.cs` → `gitcommands.py`, `gitcommands.ts` | init, status, add/rm/mv, commit, log/show, diff (py 1504–2478 / ts 1324–2217) |
| `GitMore.cs` → `gitmore.py`, `gitmore.ts` | moving between commits, branch/tag, merge, stash, plumbing and inspection, config (py 2479–3900 / ts 2218–3461) |
| `GitPatch.cs` → `gitpatch.py`, `gitpatch.ts` | patches: apply, am, format-patch; am; format-patch; bisect (py 3901– / ts 3462–) |

- C# is aligned first (phase 4, C#): `Git.cs`'s extra `objects` marker is gone (its `Commit`, `Change` and `Stat`
  types now sit above `option parsing`, as file-local types do, §4.1), and `GitCore.cs`'s `trees` marker is named
  `objects` like Python's and TS's. Python keeps `Commit` in `gitcore`'s `objects` section; C# can't move it there
  without renaming `Git.Commit`, so that difference stays. TS `gitcore.ts` folds its first section (code points and
  byte order) into `helpers` or the top of the file and drops the parenthesis from `line diff`: marker names are
  compared exactly.
- In both ports `register` moves to `gitrevisions`, as C#'s `Register`/`Run` live in `GitRevisions.cs`. Update the
  calls in `commands/__init__.py` and `commands/index.ts`.
- `HANDLERS` (the subcommand table, at the end of both files today) stays where the cut leaves it: the end of
  `gitpatch`. C# keeps its table in `GitRevisions.cs`, where load order doesn't exist; in Python and TS the table
  there would import the subcommand modules while they are loading.
- **Python import cycle.** The dispatcher needs `HANDLERS`, and `gitmore` and `gitpatch` need `gitrevisions`. `_git` does
  `from .gitpatch import HANDLERS` inside the function. The codebase already uses function-level imports to break cycles.
- **TS.** `gitrevisions.ts` imports `HANDLERS` from `gitpatch.js` and reads it only inside `git()`. The cycle is safe
  only while nothing in it uses another module's `const` or `class` at module top level; `HANDLERS`' own initialiser
  names `function` declarations only (they are hoisted). The import-alone check in §9 proves it for each entry point.
- `gitmore` comes out at about 1,450 lines and `gitpatch` at about 950 in every port, matching `GitMore.cs` and
  `GitPatch.cs`. If either should be split further, split it in all three at once.

## 7. Parsers and jq (optional, last)

- **Parser.** C# and Python are each AST, then `Lexer`, then `Parser`. TS combines lexing into its `Parser` class, so
  a three-way Lexer/Parser split would be a rewrite, not a move. The only mirrored move is to extract the AST node
  types: `Parsing/Syntax.cs`, `syntax.py`, `syntax.ts`, re-exported from the parser module. That leaves the parsers at
  about 920, 1,030 and 910 lines. The benefit is small: do it last or skip it.
- **jq.** TS splits it four ways (`jq.ts`, `jq-parse.ts`, `jq-ops.ts`, `jq-values.ts`). C# `Jq.cs` (2,127 lines) and
  `jq.py` (1,877 lines) could mirror that as `JqParse.cs` / `jq_parse.py` and so on. This is the same kind of move
  and is worth doing after phase 6.

## 8. Parity check (`tools/layout.json`, `tools/check_layout.py`)

`layout.json` holds one row per module of the **target** layout. It is written once, in phase 0, from §2, §3.2 and
§5, and the implementers don't edit it (§1): three people working from one frozen file can't drift apart, and
their branches don't conflict in it. Ports are explicit paths, never derived from a naming rule:

```json
{
  "roots": { "cs": "src/Topwater", "py": "python/src/topwater", "ts": "typescript/src" },
  "ignore": { "cs": ["bin/", "obj/", "Generated/"], "py": ["_spec.py"], "ts": ["spec.generated.ts"] },
  "legacy": { "py": ["shell.py", "commands/more.py", "commands/net.py"], "ts": ["commands/builtins.ts", "commands/xml.ts"] },
  "modules": [
    { "name": "shell.execution", "cs": "Shell/Execution.cs", "py": "shell/execution.py", "ts": "shell/execution.ts",
      "sections": ["scripts", "lists", "jobs", "pipelines", "compound commands", "conditional expressions"] },
    { "name": "commands.process", "cs": "Builtins/ProcessCommands.cs", "py": "commands/process.py", "ts": "commands/process.ts",
      "commands": ["ps", "wait", "jobs", "disown", "kill", "ulimit", "times", "umask", "sleep", "timeout", "time"] },
    { "name": "commands.bc", "cs": "Builtins/Bc.cs", "py": null, "ts": null, "note": "not ported yet", "commands": ["bc"] },
    { "name": "regex", "cs": null, "py": null, "ts": "regex.ts", "note": "the platform has no POSIX regex" }
  ]
}
```

`check_layout.py` uses only the Python standard library. It fails when:

1. A listed file doesn't exist, or a `null` has no `note`.
2. A `.cs`, `.py` or `.ts` file under a root is neither listed in a row nor in `legacy` (nor ignored). A new file
   needs its counterparts, or a row with a note explaining why it has none.
3. A file of a row with `sections` doesn't have exactly those `---------- name ----------` markers, in that order
   (a last `helpers` marker is allowed in Python and TS, §3.2). The name is the whole text between the dashes.
4. A file doesn't register exactly its row's `commands`, in that order (none, for a row without `commands`). A
   registration is a call `Register("x", …)`, `register_command("x", …)`, `register('x', …)`, `reg('x', …)` or
   `r("x", …)` whose name is a spec command, or a `for`/`foreach` line over literal names whose body (the next
   two lines) registers the loop variable; a call whose second argument is a number (jq's own functions) is not one.
   Every `spec/commands/*.json` name must belong to exactly one row, so a new command must be placed.

While a port has a `legacy` entry, checks 1, 3 and 4 print its problems as `pending` and don't fail; checks 2 and the
spec-command placement are strict from the start. The lead removes a port's entry when its last phase merges, and
the port is strict from then on. `--strict cs` shows a port's problems as errors while it still has the entry,
which is how an implementer sees what is left. The script runs in the CI `spec` job after `test_gen.py`, with its
self-test (`--self-test`, on a temporary tree, for checks 2, 3 and 4).

## 9. Order and risks

**Order.** Phase 0 is done by whoever leads, not by a port's implementer, and is reviewed before anything moves:
it fixes the file names, section names and command order for everyone. Then the C# shell split runs alone as the
pilot (the compiler proves a partial-class move is complete), and §3.2 is corrected from what it reports. After
that the three ports are independent: one implementer per port takes its remaining phases in order (shell, git,
commands, engines), each on its own branch, touching only its port's directory. `layout.json`, this file and
AGENTS.md are changed by the lead only (the phase table above is ticked when a branch merges). No feature work
lands on a file between the `pre-layout` tag and that file's move.

**Rollback.** Every commit is one phase of one port and moves only, so `git revert` of that commit is the whole
rollback, and a port can stop after any phase: the checker reports the rest as pending. Ports don't have to finish
together, but a phase isn't ticked until all three have it.

**Checks for every move commit:**
- The AGENTS.md test rows, `npm run build:browser && npm run test:browser`, and `check_layout.py`.
- A line check: the sorted non-blank lines of the old file(s) equal those of the new files, apart from
  import, using, header, marker and re-export lines (and, in TS, the signature lines that gained `this: Shell`).
  For example:
  `diff <(git show HEAD~:python/src/topwater/shell.py | sort) <(cat python/src/topwater/shell/*.py | sort)`.
- The surface is the same before and after. Python: `sorted(dir(Shell))` equal, and every name in the old
  `dir(topwater.shell)` still there. TS: `Object.getOwnPropertyNames(Shell.prototype).sort()` equal, nothing
  enumerable on the prototype, and the non-`@internal` members of `dist/shell.d.ts` equal. C#: a reflection dump of
  every type and member of `Topwater.dll`, all visibilities, compiler-generated names left out, is identical before
  and after, and so are the `<member>` entries of `Topwater.xml` (sorted).
- Each new module imports alone, as the first import of a fresh process (this is what finds import cycles):
  `python -c "import topwater.glob"`, `… topwater.shell.expansion`, `… topwater.commands.gitmore` and so on;
  `node -e "await import('./dist/shell/execution.js')"` and so on after `npm run build`.
- Python: `pipx run pyflakes python/src/topwater` reports no new undefined names, and no part repeats a name:
  `python -c "from topwater.shell import Shell; import collections as c; d=c.Counter(n for k in Shell.__mro__[1:-1] for n in vars(k) if n[:2]!='__'); assert max(d.values())==1, [n for n in d if d[n]>1]"`.
- Commands (phase 5): on a new shell with a fixed start time and random seed, the output of
  `help; ls -la /usr/bin /bin; type -a ls echo cd; hash; date; stat /usr/bin/ls`, each command's description and help,
  and the entries of a saved snapshot (names and contents in order) are the same before and after. Not the snapshot's
  bytes: the zip stamps each entry with the host's clock, so two saves of one shell differ.
- Review with `git diff -M --color-moved=dimmed-zebra`.

| Risk | Port | Mitigation |
|---|---|---|
| A name a moved function uses isn't imported in its new module. Python only fails when that path runs | Python | pyflakes undefined-name check, line check, full suite and corpus |
| Import cycles between the parts, `__init__` and the root modules the parts import | Python | Parts never import `topwater.shell`; `glob.py` imports nothing from it at module level (§3.3); the import-alone check |
| `_RUNNING` (a `ContextVar`) defined twice | Python | Defined once in `glob.py`; everything else imports it |
| A test or host monkeypatches `topwater.shell.X` | Python | None today (checked `python/tests`); every old module-level name is re-exported |
| Two parts define the same method | Python | The one-line check above |
| Prototype methods made enumerable, or a getter copied as a value | TS | Descriptors are copied (§3.3); accessors stay in the class; the surface check |
| `.d.ts` shows former `private` members with their types | TS | They carry `/** @internal */` like the 111 that are public for the same reason today. `stripInternal` is a separate decision; class parts keep it possible (§3.3) |
| Type-inference cycles through `Shell` | TS | Explicit return type where `tsc` asks |
| Runtime cycle at module load (`class X extends Y` across files, a `const` read at top level) | TS | `shell/types.ts` is a leaf. Writer subclasses stay together in `redirection.ts`. The import-alone check |
| Static or instance field initialisers split across files of the partial class run in an undefined order | C# | All fields stay in `Shell/Shell.cs`; a static that reads another static stays below it in the same file |
| Command registration order changes (`_commands` insertion order) | all | `help` and `AppCommandNames` sort, and no name is registered twice. The before/after check above |
| Missing usings | C# | The compiler catches them; `ImplicitUsings` is on |
| Rebasing open branches across a split | all | The freeze above. Use `git log --follow` and `git blame -C -C -M` for history |
| Python packaging misses `topwater.shell`, or a stale `shell.py` sits next to `shell/` | Python | `packages.find` picks up any directory with `__init__.py`; check the built wheel once; the checker fails on an unlisted `shell.py` once `legacy` is gone |
