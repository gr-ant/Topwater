# Web access

The machine has no network unless you allow one. `AllowWeb` lets the agent's `curl` and `wget` reach the sites you
list, and nothing else. The command line tool `topwater` has no option for it: its machine stays offline.

- [Allowing sites](#allowing-sites)
- [curl and wget](#curl-and-wget)
- [Blocked requests](#blocked-requests)
- [What it guarantees](#what-it-guarantees)
- [What it does not guarantee](#what-it-does-not-guarantee)
- [The tool description](#the-tool-description)
- [Recordings](#recordings)
- [In the browser](#in-the-browser)

## Allowing sites

`curl` and `wget` are always there, but without a policy every host fails to resolve, as on a machine with no
network. `AllowWeb` lets them reach the sites you list:

```csharp
shell.AllowWeb(new WebPolicy
{
    Domains = ["api.github.com", "*.wikipedia.org"],   // *.x matches subdomains of x, not x itself; name:8443 for another port
    Methods = ["GET"],                                  // the default; GET also allows HEAD
    Credentials = { ["api.github.com"] = () => $"Bearer {secrets.Get("github")}" },
});
```

```python
shell.allow_web(WebPolicy(domains=["api.github.com", "*.wikipedia.org"],
                          credentials={"api.github.com": lambda: "Bearer " + token}))
```

```ts
shell.allowWeb({ domains: ['api.github.com', '*.wikipedia.org'], credentials: { 'api.github.com': () => `Bearer ${token}` } });
```

| Option | Default | |
|---|---|---|
| `Domains` | none | host names, exact or `*.name`, on port 443 (80 for http); `name:port` allows that port instead |
| `Methods` | `GET` | |
| `AllowHttp` | false | https only unless set |
| `MaxResponseBytes` | 10,000,000 | read as it streams; the command stops there (`curl` exit 63) |
| `MaxRequestsPerCall`, `MaxRequests` | 20, 500 | per tool call and per shell; every redirect counts |
| `Timeout` | 30 s | per request; the call's own limit still applies |
| `MaxRedirects` | 10 | `curl --max-redirs` and `wget --max-redirect` can only lower it |
| `Credentials` | none | the `Authorization` header for an exact host (`host:port` for another port), added in your code |
| `ExplainBlocked` | true | false: a refused host fails like an unknown one instead of saying why |
| `Handler` (`transport` in Python and TypeScript) | the network | send requests your own way (tests, proxies); must not follow redirects or keep cookies (C# refuses `AllowAutoRedirect` and `UseCookies`), and should set `UseProxy` deliberately |

Python spells the options in snake_case (`max_response_bytes`, `timeout` in seconds) and TypeScript in camelCase
(`maxResponseBytes`, `timeoutMs`). `Fork()` does not inherit web access.

## curl and wget

`curl` takes `-s -S -f --fail-with-body --fail-early -L --max-redirs -i -I -o -O -X -H -d --data-raw --data-binary
--data-urlencode --json -G -u -A -w -m --compressed -v --retry --retry-delay --retry-max-time --retry-all-errors
--retry-connrefused --proto --proto-redir` (`@file` and `@-` work as in curl; `-w` knows `http_code`, `response_code`,
`content_type`, `size_download`, `size_upload`, `size_header`, `size_request`, `speed_download`, `speed_upload`,
`url`, `url_effective`, `redirect_url`, `num_redirects`, `num_connects`, `method`, `scheme`, `http_version`,
`remote_port`, `filename_effective`, `errormsg`, `exitcode` and the `time_*` values). `wget` takes `-O` (`-O -`), `-q`,
`-nv`, `-S`, `-P`, `-nc`, `--spider`, `--header`, `--post-data`, `--post-file`, `-U`, `-T` and `--max-redirect`.
Output, error messages and exit codes match curl 8.5 and wget 1.21 on Ubuntu 24.04 for the cases in
[`tests/web`](../tests/web/README.md), recorded from the real programs; all three implementations run them.
Differences:
no progress meter, a shorter `wget` report (no Resolving/Connecting lines, dots or speeds, no retries), `-v` shows
only the request and response headers, `-k` / `--no-check-certificate` are accepted and ignored (certificates are
always checked), and so are the TLS and HTTP version options (`--tlsv1.2`, `--http1.1`, ...), progress options
(`--no-progress-meter`, `-#`, wget `--show-progress`) and wget `-t`. `curl --retry` waits as curl does (1 second,
doubling, or longer when the server's `Retry-After` asks for it) and every wait counts toward the call's time limit;
each try is a request under the policy's limits, and a request the policy refuses with a reason (`ExplainBlocked`) is
not sent again. `-w` times come from the shell's clock and are measured around the whole request: `time_namelookup`,
`time_connect`, `time_appconnect` and `time_pretransfer` read 0, and on a virtual clock (`StartTime`) every time
reads `0.000000` and the speeds `0`, so recordings replay exactly.

## Blocked requests

A host outside the policy says so: `curl: (6) Blocked by the web policy: evil.example is not an allowed host
(allowed: api.github.com, *.wikipedia.org)` (the first ten sites; wget exit 4), so the agent stops trying instead of
reporting a broken site. An IP address or a port that is not listed gets exit 7 the same way. A shell with no policy is
an offline machine: `curl: (6) Could not resolve host`. With `ExplainBlocked = false` a policy's refusals look like that
too. A method the policy does not allow, or a reached request limit, always says so (exit 1).

## What it guarantees

Even against a hostile agent:

- **Only your hosts.** The URL is parsed one way in all three implementations: user info never becomes the host
  (`https://allowed.com@evil.com/` is evil.com), IPv4 in any form (`2130706433`, `0x7f.1`, `0177.0.0.1`) is
  normalized first, and every redirect is checked like the first URL.
- **No internal addresses.** A name is resolved once; if any address is loopback, private, link-local (169.254.169.254,
  the cloud metadata service), Azure's 168.63.129.16, CGNAT, multicast, reserved, unspecified, documentation, IPv6
  unique-local, site-local, discard or ORCHID, or one of these IPv4 ranges inside IPv6 (`::ffff:`, NAT64, 6to4), the
  request is refused. Otherwise the connection goes to the
  address that was checked, while TLS and `Host` use the name, so DNS rebinding cannot swap it. IP literals are
  refused the same way, even when listed in `Domains`. No policy setting turns this off; only test hooks inside the
  library can.
- **Nothing of yours leaks.** No proxy settings, cookies or default headers of your process are used. The agent's
  `Host`, `Content-Length`, `Transfer-Encoding`, `Connection` and `Proxy-*` headers are dropped, header values cannot
  hold line breaks, and its `Authorization` (and `-u`) for a host with a credential is replaced by yours. Its own
  `Authorization` and `Cookie` do not follow a redirect to another scheme, host or port.
- **The credential stays hidden,** as with [`RegisterApi`](programs.md#authenticated-api-calls): not in `-v`,
  scrubbed from response headers and bodies,
  and never stored in a recording. A host's credential goes only to the port it was given for. For such a host a
  gzip or deflate body is decoded before the scrub (another encoding fails the request), and the agent's `Range`
  and `If-Range` are dropped, so it cannot fetch the credential compressed or in pieces. The scrub finds the
  credential as written, not a transformed copy (base64, reversed, split across JSON strings): do not give a
  credential to a host that returns it in another form.
- **Bounded.** Response bytes, response headers (64 KB), request bodies (1,000,000 bytes), requests, redirects and
  time are capped. Bodies reach the virtual filesystem only through normal output and `-o`.

## What it does not guarantee

The agent can read anything on the sites you allow and send what it read elsewhere to
any allowed site with an allowed method. Keep `Methods` to `GET` unless writes are the point, and list exact hosts.

A `Handler` that connects somewhere itself skips the address check, which happens where names are resolved.

## The tool description

The default tool description follows the policy: `BashTool.DescriptionFor(shell)` (`tool_description(shell)`,
`BashTool.descriptionFor(shell)`), which every integration uses unless you pass your own, names the sites (the first
20) and methods in place of "network access is not available".

## Recordings

A shell with web access records every request and its response (status, headers and the body as a
blob, after credentials are scrubbed) with the call, and the policy without its credentials in the header. A shell
built from the recording (`CreateShell`, `ShellAtAsync`, `ReplayAsync`, the viewer) serves those responses in order
and never touches the network; a request that was not recorded fails with `Topwater replay: GET URL is not in the
recording`, which shows up as a difference.
The format is in [spec/topwater-file](../spec/topwater-file/README.md#web-exchanges).

## In the browser

The TypeScript build in a browser uses `fetch` under the same policy, with two limits: the browser resolves
names itself, so internal addresses can be refused only when written as IP literals, and a site must allow the page's
origin with CORS, or the request fails like a broken connection. Without `-L`, a redirect cannot be seen
(`fetch` hides it) and fails the same way.
