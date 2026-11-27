# Topwater.Python

`python3` for a [Topwater](https://github.com/gr-ant/Topwater) shell: real CPython 3.14, compiled to WebAssembly, that
reads and writes the shell's virtual files and nothing else. HTTP from `urllib`, `requests` and `httpx` goes through the
shell's web policy, or nowhere.

```bash
dotnet add package Topwater.Python --prerelease
```

```csharp
using Topwater;
using Topwater.Python;

var shell = new Shell().AllowPython();   // or AllowPython(new PythonOptions { MaxMemory = 128L << 20 })
var r = await shell.RunAsync("python3 -c 'import json; print(json.dumps({\"ok\": True}))'");
```

The interpreter is [bashkit](https://github.com/everruns/bashkit)'s CPython build (MIT), carried in this assembly and
checked against pinned SHA-256 hashes before use. It runs on [Wasmtime](https://www.nuget.org/packages/Wasmtime) over
Topwater's own WASI implementation: no host file, socket, process or thread is reachable. The first `AllowPython` in a
process compiles it (seconds); set `PythonOptions.CacheDir` to keep the compiled code between processes.

Limits, the security model and the differences from Ubuntu's `python3` are in
[docs/python.md](https://github.com/gr-ant/Topwater/blob/main/docs/python.md). The interpreter's notices (CPython, zlib,
SQLite, libmpdec, Expat, HACL*, wasi-libc) are in the package's `THIRD-PARTY-NOTICES.md` and `python-wasm/` folder.
