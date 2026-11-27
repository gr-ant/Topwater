# topwater-python

The interpreter behind [Topwater](https://github.com/gr-ant/Topwater)'s opt-in `python3` command: CPython 3.14
compiled to WebAssembly (built by [bashkit](https://github.com/everruns/bashkit)) and its standard library, run by
[wasmtime](https://pypi.org/project/wasmtime/). Install it through the extra, then enable the command:

```
pip install 'topwater[python]'
```

```python
from topwater import Shell

shell = Shell().allow_python()
print(shell.run("python3 -c 'print(sum(range(10)))'").stdout)
```

The program sees the shell's virtual files and nothing of the host. See
[docs/python.md](https://github.com/gr-ant/Topwater/blob/main/docs/python.md) for what works, the limits and the
security model. The artifacts' licences are in `src/topwater_python/interpreter/THIRD_PARTY_LICENSES.md`.
