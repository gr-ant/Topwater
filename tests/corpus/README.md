# Shared test corpus

One copy of the recorded shell behaviour, run by all three implementations: C# (`tests/Topwater.Tests/CorpusTests.cs`),
Python (`python/tests/test_corpus.py`) and TypeScript (`typescript/test/corpus.test.ts`). Change behaviour here, never in
a port's own copy, so the implementations cannot drift apart.

## Format

One file per area, `<area>.jsonl`, one case per line:

```json
{"id":"arithmetic-004","script":"x=5; echo $(( x > 3 ? 10 : 20 ))","stdout":"10\n","exitCode":0,"quiet":true}
```

| Field | | Meaning |
|---|---|---|
| `id` | required | `<area>-<NNN>`, unique and stable; new cases take the next number |
| `script` | required | what the agent types, run as one call |
| `stdout` | required unless `toolOutput` | exact standard output |
| `exitCode` | optional | exact exit status; `null` or absent: not checked |
| `quiet` | optional | `true`: nothing on stderr, `false`: something on stderr (the text is not compared, since Topwater's messages need not match bash's word for word); `null` or absent: not checked |
| `setup` | optional | a script run first in the same shell; its result is ignored |
| `toolOutput` | optional | compare the whole tool output (`ToToolOutput()`: stdout, stderr, `[exit code N]`) instead of `stdout`/`exitCode`/`quiet` |
| `tags`, `notes` | optional | free-form; not used by the runners |

Every runner does the same thing per case: a fresh `Shell` with default options, `mkdir -p /tmp/w && cd /tmp/w`, then
`setup` (if any), then `script`, then the checks above.

## Where the expected values come from

Each value is what the real program printed, recorded by running the script in real bash in an empty directory:
bash 5.2.21 and GNU coreutils 9.4 for the shell areas, GNU date 9.4 with `TZ=UTC` (`date`), GNU bc 1.07.1 (`bc`), GNU
tar 1.35, gzip 1.12, Zip 3.0 and UnZip 6.00 (`archives`; compressed bytes differ from the real tools', so these cases
compare listings, messages and round-tripped contents, never sizes). These are the versions on Ubuntu 24.04. Exceptions:

- `bc-094`..`bc-096` are Topwater's safety caps (GNU bc would hang or run out of memory): no output, an error, exit 1.
- `shell-syntax`, `agent-features`, `second-review`, `array-tests` and `review-fixes` came from older hand-written
  tables checked against bash; they leave `exitCode` or `quiet` unchecked where those tables did, and `review-fixes`
  compares the tool output after a shared `setup`.

The corpus was generated once from the C# `[InlineData]` tables (November 2026); those tables are gone.

## Adding a case

```bash
python3 tests/corpus/record.py quoting 'x="a  b"; echo $x'        # runs it in ubuntu:24.04 under docker, appends the case
python3 tests/corpus/record.py bc --apt bc "echo 'scale=3; 1/7' | bc"   # installs packages the image lacks first
python3 tests/corpus/record.py loops --local 'for i in 1 2; do echo $i; done'   # host bash in a temp dir, no docker
python3 tests/corpus/record.py quoting --dry-run 'echo hi'          # print the case, don't append
```

The tool runs the script as `bash -c` in an empty `/tmp/w` (`TZ=UTC`, `LC_ALL=C.UTF-8`, no network), and appends
`{id, script, stdout, exitCode, quiet}` with the real output. Keep scripts independent of the machine: no `$USER`,
`$HOME`, `$PWD` beyond `/tmp/w`, hostnames, clocks or PIDs, since the simulated machine differs there on purpose. Then run
the three suites; a case a port cannot pass yet goes in its skip list.

## Skip lists

`skip/csharp.json`, `skip/python.json`, `skip/typescript.json` map a case id, or a whole area, to the reason that
implementation does not pass it yet:

```json
{ "bc": "bc is not ported yet", "archives-042": "zip prints zlib's compression ratio, not Info-ZIP's" }
```

Never change an expected value to make a port pass; fix the port or skip the case with a reason. Each runner fails if a
skip key names no case or area.
