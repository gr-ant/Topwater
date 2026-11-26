# Web cases

`curl` and `wget` against a test web server, run by all three ports through a fake transport (C#
`WebAccessTests.cs`, Python `test_web.py`, TypeScript `web.test.ts`), so none of them needs the network.

| File | |
|---|---|
| `routes.json` | what the server answers per path; `/echo` answers with the request (method and target, then the `content-type`, `accept`, `authorization`, `user-agent` and `x-test` headers it got, a blank line, the body). Every response ends its headers with `Content-Length`, except a `raw` route: exactly those bytes (Latin-1), then the connection closes |
| `server.py` | that server over plain HTTP, for recording |
| `cases.jsonl` | `{id, script, stdout, stderr, exitCode}`; `stderr: null` is not compared. `wire: true`: the case needs a `raw` route (a malformed response). `note`: Topwater differs from the real tool on purpose, as the note says; the case holds Topwater's output and is not re-recorded |
| `record.py` | re-records the cases from real curl 8.5.0 and wget 1.21.4 on ubuntu:24.04 (docker) |

The ports run each case on a fresh shell, in `/tmp/w`, with the policy `Domains = ["api.test"]`, `AllowHttp`, and
the methods GET, POST, PUT, DELETE and PATCH. The recording container has no network and knows only `api.test`
(127.0.0.1), so a host outside that policy fails there the same way it does in Topwater, and redirects to
`127.0.0.1:9` or `169.254.169.254` fail to connect in both.

A fake transport cannot send a malformed response, so the ports run the `wire` cases over a real loopback socket: a
small server of their own serves the `raw` routes, the script's `http://api.test/` gets that server's port, and the
policy's transport (C# handler) is the real pinned one with a test-only resolver that lets loopback through.
