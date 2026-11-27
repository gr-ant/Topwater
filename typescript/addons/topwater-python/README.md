# topwater-python

The interpreter behind [Topwater](https://github.com/gr-ant/Topwater)'s opt-in `python3` command: CPython 3.14
compiled to WebAssembly (built by [bashkit](https://github.com/everruns/bashkit)) and its standard library. It has no
dependencies of its own: Topwater runs it with the WebAssembly support every JavaScript runtime has.

```
npm install topwater topwater-python
```

```js
import { Shell } from 'topwater';

const shell = await new Shell().allowPython();
console.log((await shell.run("python3 -c 'print(sum(range(10)))'")).stdout);
```

The program sees the shell's virtual files and nothing of the host. It runs in a worker that the shell stops at the
call's time limit. Node 22.3 or later needs nothing more; a browser page must be cross-origin isolated (served with
`Cross-Origin-Opener-Policy: same-origin` and `Cross-Origin-Embedder-Policy: require-corp`), or `allowPython` rejects.
See [docs/python.md](https://github.com/gr-ant/Topwater/blob/main/docs/python.md) for what works, the limits and the
security model. The artifacts' licences are in `interpreter/THIRD_PARTY_LICENSES.md`.
