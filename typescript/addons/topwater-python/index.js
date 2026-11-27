// The interpreter for Topwater's opt-in python3 command: bashkit's CPython 3.14 build for wasm32-wasip1 (gzip) and its
// standard library, in interpreter/ (from the repository's assets/python-wasm). Topwater finds it here when the host
// calls shell.allowPython() without an artifacts folder; a bundler that doesn't keep this folder next to the code can
// serve the two files itself and pass their folder's URL as `artifacts`.
export const artifacts = new URL('./interpreter/', import.meta.url);
